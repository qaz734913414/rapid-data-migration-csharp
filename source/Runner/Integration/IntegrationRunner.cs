﻿using JHWork.DataMigration.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// IntegrationRunner 的实例结构如下：
//
// IntegrationInstance
//   + IntegrationTask[][] (并行)
//      + IntegrationTable
//
// 即，每个实例包含一组串行任务（二维数组结构用于解决表之间的依赖关系），每个任务可包含一组并行汇集表

namespace JHWork.DataMigration.Runner.Integration
{
    /// <summary>
    /// 汇集执行器
    /// </summary>
    public class IntegrationRunner : IAssemblyLoader, IRunnerAnalyzer, IRunnerExecutor, IRunnerAssistant
    {
        private IStopStatus status;

        private Database AnalyseDatabase(JObject obj)
        {
            return new Database()
            {
                DBMS = obj["dbms"].ToString(),
                Server = obj["server"].ToString(),
                Port = uint.Parse(obj["port"].ToString()),
                DB = obj["db"].ToString(),
                User = obj["user"].ToString(),
                Pwd = obj.ContainsKey("password") ? obj["password"].ToString() : "",
                CharSet = obj.ContainsKey("charset") ? obj["charset"].ToString() : "utf8",
                Encrypt = obj.ContainsKey("encrypt") ? int.Parse(obj["encrypt"].ToString()) != 0 : false,
                Compress = obj.ContainsKey("compress") ? int.Parse(obj["compress"].ToString()) != 0 : false
            };
        }

        private Database[] AnalyseDatabases(JArray objs)
        {
            List<Database> lst = new List<Database>();

            foreach (JObject obj in objs)
            {
                JArray dbs = obj["dbs"] as JArray;

                foreach (JToken db in dbs)
                    lst.Add(new Database()
                    {
                        DBMS = obj["dbms"].ToString(),
                        Server = obj["server"].ToString(),
                        Port = uint.Parse(obj["port"].ToString()),
                        DB = db.ToString(),
                        User = obj["user"].ToString(),
                        Pwd = obj.ContainsKey("password") ? obj["password"].ToString() : "",
                        CharSet = obj.ContainsKey("charset") ? obj["charset"].ToString() : "utf8",
                        Encrypt = obj.ContainsKey("encrypt") ? int.Parse(obj["encrypt"].ToString()) != 0 : false,
                        Compress = obj.ContainsKey("compress") ? int.Parse(obj["compress"].ToString()) != 0 : false
                    });
            }

            return lst.ToArray();
        }

        public Instance[] AnalyseInstance(JArray objs, string path)
        {
            List<IntegrationInstance> instances = new List<IntegrationInstance>();

            foreach (JObject obj in objs)
            {
                IntegrationTable[][] tables = AnalyseTable($"{path}\\{obj["tables"]}", out string parms);
                IntegrationTask[][] tasks = new IntegrationTask[tables.Length][];
                List<IntegrationTask> taskList = new List<IntegrationTask>();

                for (int i = 0; i < tasks.Length; i++)
                {
                    if (tables[i].Length > 0)
                    {
                        tasks[i] = new IntegrationTask[tables[i].Length];
                        for (int j = 0; j < tasks[i].Length; j++)
                        {
                            IntegrationTask task = new IntegrationTask()
                            {
                                Name = tables[i][j].DestName,
                                Progress = 0,
                                Total = 0,
                                Status = DataStates.Idle,
                                StartTick = 0,
                                Sources = AnalyseDatabases(obj["sources"] as JArray),
                                Dest = AnalyseDatabase(obj["dest"] as JObject),
                                Params = parms,
                                Table = tables[i][j],
                                ReadPages = uint.Parse(obj["readPages"].ToString())
                            };

                            taskList.Add(task);
                            tasks[i][j] = task;
                        }
                    }
                }

                if (taskList.Count > 0)
                {
                    IntegrationInstance ins = new IntegrationInstance()
                    {
                        Name = $"{taskList[0].Dest.Server}/{taskList[0].Dest.DB}",
                        Tasks = taskList.ToArray(),
                        ActualTasks = tasks,
                        Threads = uint.Parse(obj["threads"].ToString())
                    };

                    instances.Add(ins);
                }
            }

            return instances.ToArray();
        }

        private IntegrationTable[][] AnalyseTable(string file, out string parms)
        {
            JObject obj = LoadAndDeserialize(file);
            JArray tables = obj["tables"] as JArray;
            List<IntegrationTable> buf = new List<IntegrationTable>();

            parms = obj["params"].ToString();

            // #1: 获取所有表
            for (int i = 0; i < tables.Count; i++)
            {
                JObject o = tables[i] as JObject;
                string[] names = o["name"].ToString().Split(',');
                IntegrationTable table = new IntegrationTable()
                {
                    SourceName = names[0],
                    DestName = names.Length > 1 ? names[1] : names[0],
                    Order = int.Parse(o["order"].ToString()),
                    OrderSQL = o["orderSQL"].ToString(),
                    SourceWhereSQL = o["whereSQL"].ToString(),
                    DestWhereSQL = o.ContainsKey("destWhereSQL") ? o["destWhereSQL"].ToString()
                        : o["whereSQL"].ToString(),
                    PageSize = uint.Parse(o["pageSize"].ToString()),
                    WriteMode = "UPDATE".Equals(o["mode"].ToString().ToUpper()) ? WriteModes.Update
                        : WriteModes.Append,
                    KeyFields = o["keyFields"].ToString().Split(','),
                    SkipFields = o["skipFields"].ToString().Split(','),
                    Filter = o["filter"].ToString(),
                    KeepIdentity = o.ContainsKey("dest.mssql.keepIdentity") ?
                        int.Parse(o["dest.mssql.keepIdentity"].ToString()) != 0 : true,
                    Progress = 0
                };

                buf.Add(table);

                if (table.WriteMode == WriteModes.Update && "".Equals(table.KeyFields[0]))
                    throw new Exception($"表 {table.SourceName} 配置有误！更新模式必须指定主键字段(keyFields)。");
                if ("".Equals(table.OrderSQL))
                    throw new Exception($"表 {table.SourceName} 配置有误！必须指定稳定的排序规则(orderSQL)。");
                if (table.PageSize <= 0)
                    throw new Exception($"表 {table.SourceName} 配置有误！批量记录数必须大于零(pageSize)。");
            }

            // #2: 生成结构
            if (buf.Count > 0)
            {
                buf.Sort(new TableComparer());

                int order = buf[0].Order;
                List<IntegrationTable> tmpBuf = new List<IntegrationTable>() { buf[0] };
                List<List<IntegrationTable>> rstBuf = new List<List<IntegrationTable>>() { tmpBuf };

                for (int i = 1; i < buf.Count; i++)
                {
                    if (buf[i].Order != order)
                    {
                        tmpBuf = new List<IntegrationTable>() { buf[i] };
                        rstBuf.Add(tmpBuf);
                        order = buf[i].Order;
                    }
                    else
                        tmpBuf.Add(buf[i]);
                }

                IntegrationTable[][] rst = new IntegrationTable[rstBuf.Count][];
                for (int i = 0; i < rstBuf.Count; i++)
                    rst[i] = rstBuf[i].ToArray();

                return rst;
            }
            else
                return new IntegrationTable[][] { };
        }

        private bool Connect(Database db, out IDBMSReader reader)
        {
            reader = DBMSFactory.GetDBMSReaderByName(db.DBMS);
            if (reader == null)
            {
                Logger.WriteLog("系统", $"数据库类型 {db.DBMS} 不支持！");
                return false;
            }
            else
                return reader.Connect(db);
        }

        private bool Connect(Database db, out IDBMSWriter writer)
        {
            writer = DBMSFactory.GetDBMSWriterByName(db.DBMS);
            if (writer == null)
            {
                Logger.WriteLog("系统", $"数据库类型 {db.DBMS} 不支持！");
                return false;
            }
            else
                return writer.Connect(db);
        }

        private static string[] CreateThreadAction()
        {
            return new string[] { "read", "write" };
        }

        private void DuplicateDatabase(Database source, Database dest)
        {
            dest.DBMS = source.DBMS;
            dest.Server = source.Server;
            dest.Port = source.Port;
            dest.DB = source.DB;
            dest.User = source.User;
            dest.Pwd = source.Pwd;
            dest.CharSet = source.CharSet;
            dest.Encrypt = source.Encrypt;
            dest.Compress = source.Compress;
        }

        public void Execute(Instance ins, IStopStatus status)
        {
            this.status = status;

            if (ins is IntegrationInstance instance)
            {
                foreach (IntegrationTask[] tasks in instance.ActualTasks)
                {
                    if (status.IsStopped()) break;

                    Parallel.ForEach(tasks, new ParallelOptions() { MaxDegreeOfParallelism = (int)instance.Threads },
                        task =>
                    {
                        task.StartTick = WinAPI.GetTickCount();
                        task.Status = DataStates.Running;
                        try
                        {
                            IntegrateTask(task, out string reason);

                            if (task.Status == DataStates.Done)
                            {
                                Logger.WriteLog($"{task.Dest.Server}/{task.Dest.DB}.{task.Table.DestName}", "汇集成功。");
                                Logger.WriteRpt(task.Dest.Server, task.Dest.DB, task.Table.DestName, "成功",
                                    task.Table.Progress.ToString("#,##0"));
                            }
                            else
                            {
                                task.Progress -= task.Table.Progress;
                                Logger.WriteLog($"{task.Dest.Server}/{task.Dest.DB}.{task.Table.DestName}",
                                    $"汇集失败！{reason}");
                                Logger.WriteRpt(task.Dest.Server, task.Dest.DB, task.Table.DestName, "失败", reason);
                            }
                        }
                        catch (Exception ex)
                        {
                            task.Status = DataStates.Error;
                            Logger.WriteLog($"{task.Dest.Server}/{task.Dest.DB}", $"汇集失败！{ex.Message}");
                        }
                        task.StartTick = WinAPI.GetTickCount() - task.StartTick;
                    });

                }

                foreach (Common.Task task in instance.Tasks)
                    if (task.Status != DataStates.Done)
                    {
                        Logger.WriteLog(instance.Name, "汇集失败！");
                        return;
                    }

                Logger.WriteLog(instance.Name, "汇集成功。");
            }
        }

        public void LoadSample(Instance ins, Database source, Database dest, List<Table> tables, out string param)
        {
            if (ins.Tasks[0] is IntegrationTask task)
            {
                DuplicateDatabase(task.Sources[0], source);
                DuplicateDatabase(task.Dest, dest);
                param = task.Params;
            }
            else
                param = "";

            foreach (Common.Task t in ins.Tasks)
                if (t is IntegrationTask ta) tables.Add(ta.Table);
        }

        public void Prefetch(Instance ins, IStopStatus status)
        {
            this.status = status;

            foreach (Common.Task t in ins.Tasks)
            {
                if (status.IsStopped()) break;

                if (t is IntegrationTask task)
                {
                    task.Progress = 0;
                    task.Total = 0;
                    task.Status = DataStates.Running;
                    task.Table.Progress = 0;

                    if (Connect(task.Dest, out IDBMSWriter dest))
                    {
                        Dictionary<string, object> parms = new Dictionary<string, object>();

                        dest.QueryParam(task.Params, parms);

                        Parallel.ForEach(CreateThreadAction(), act =>
                        {
                            if ("read".Equals(act))
                            {
                                IDBMSReader source = null;

                                foreach (Database db in task.Sources)
                                {
                                    if (task.Status != DataStates.Error && !status.IsStopped())
                                        if (Connect(db, out source))
                                        {
                                            bool isError = true;

                                            // #1: 检查表存在，并缓存字段
                                            if (source.GetFieldNames(task.Table.SourceName, out string[] fields))
                                            {
                                                task.Table.SourceFields = fields;

                                                // #2: 获取记录数
                                                if (source.QueryCount(task.Table.SourceName, task.Table.SourceWhereSQL,
                                                    WithEnums.NoLock, parms, out ulong count))
                                                {
                                                    task.Total += count;
                                                    isError = false;
                                                }
                                            }

                                            if (isError) task.Status = DataStates.Error;
                                            source.Close();
                                        }
                                        else
                                            task.Status = DataStates.Error;
                                }
                            }
                            else if ("write".Equals(act))
                            {
                                if (dest.GetFieldNames(task.Table.DestName, out string[] fields))
                                    task.Table.DestFields = fields;
                                else
                                    task.Status = DataStates.Error;
                                dest.Close();
                            }
                        });
                        if (task.Status != DataStates.Error) task.Status = DataStates.Idle;
                    }
                    else
                        task.Status = DataStates.Error;
                }
            }
        }

        public string GetName()
        {
            return "Integration";
        }

        private void IntegrateTask(IntegrationTask task, out string reason)
        {
            reason = "取消操作";
            if (status.IsStopped()) return;

            if (Connect(task.Dest, out IDBMSWriter dest))
            {
                Dictionary<string, object> parms = new Dictionary<string, object>();

                dest.QueryParam(task.Params, parms);
                dest.BeginTransaction();
                try
                {
                    // 汇集数据
                    IntegrateTaskWithScript(task, parms, dest, out reason);

                    if (task.Status == DataStates.Done)
                        dest.CommitTransaction();
                    else
                        dest.RollbackTransaction();
                }
                catch (Exception ex)
                {
                    dest.RollbackTransaction();
                    task.Status = DataStates.Error;
                    reason = ex.Message;
                }

                dest.Close();
            }
            else
            {
                task.Status = DataStates.Error;
                reason = "连接失败！";
            }
        }

        private void IntegrateTaskWithScript(IntegrationTask task, Dictionary<string, object> parms, IDBMSWriter dest,
            out string failReason)
        {
            ConcurrentQueue<object> scripts = new ConcurrentQueue<object>();
            bool read = false;
            string reason = "取消操作";

            Parallel.ForEach(CreateThreadAction(), act =>
            {
                try
                {
                    // “读”线程：个别数据源读取失败，容错继续执行
                    if ("read".Equals(act))
                    {
                        uint bufSize = task.ReadPages * 3;
                        IDataFilter filter = DataFilterFactory.GetFilterByName(task.Table.Filter);
                        IDataWrapper data = null;

                        foreach (Database db in task.Sources)
                        {
                            if (status.IsStopped() || task.Status == DataStates.Error) break;

                            uint fromRow = 1, toRow = task.ReadPages * task.Table.PageSize;
                            Dictionary<string, object> tmpParams = new Dictionary<string, object>(parms);

                            // 连接数据源
                            if (!Connect(db, out IDBMSReader source))
                            {
                                task.Status = DataStates.RunningError;
                                reason = "连接失败！";
                                break;
                            }

                            while (true)
                            {
                                // 等待缓冲区可用
                                while (scripts.Count > bufSize && !status.IsStopped() && task.Status != DataStates.Error)
                                    Thread.Sleep(50);

                                if (status.IsStopped() || task.Status == DataStates.Error) break;

                                // 取数
                                if (source.QueryPage(task.Table, fromRow, toRow, WithEnums.NoLock, tmpParams, out data))
                                    try
                                    {
                                        object script = null;

                                        data.MapFields(task.Table.DestFields);
                                        while (dest.BuildScript(task.Table, data, filter, out script)
                                            && !status.IsStopped() && task.Status != DataStates.Error)
                                            scripts.Enqueue(script);

                                        // 获取不到预期的记录数，作最后一页处理
                                        if (data.ReadCount != task.ReadPages * task.Table.PageSize
                                            || status.IsStopped()) break;
                                    }
                                    finally
                                    {
                                        data.Close();
                                    }
                                else
                                {
                                    task.Status = DataStates.RunningError;
                                    reason = source.GetLastError();
                                    break;
                                }

                                fromRow = toRow + 1;
                                toRow += task.ReadPages * task.Table.PageSize;
                            }

                            source.Close();
                        }
                        read = true;
                    }
                    // “写”线程：写失败则直接停止执行
                    else if ("write".Equals(act))
                    {
                        while (task.Status != DataStates.Error && (!read || scripts.Count > 0) && !status.IsStopped())
                            if (scripts.Count > 0)
                            {
                                scripts.TryDequeue(out object script);
                                if (!dest.ExecScript(task.Table, script, out uint r))
                                {
                                    task.Status = DataStates.Error;
                                    reason = dest.GetLastError();
                                    break;
                                }

                                lock (task)
                                {
                                    task.Table.Progress += r;
                                    task.Progress += r;
                                }
                            }
                            else
                                Thread.Sleep(1);
                    }
                }
                catch (Exception ex)
                {
                    task.Status = DataStates.Error;
                    reason = ex.Message;
                }
            });

            if (task.Status == DataStates.Error || task.Status == DataStates.RunningError || status.IsStopped())
            {
                task.Status = DataStates.Error;
                failReason = reason;
            }
            else
            {
                task.Status = DataStates.Done;
                failReason = "";
            }
        }

        private JObject LoadAndDeserialize(string file)
        {
            return JsonConvert.DeserializeObject(File.ReadAllText(file)) as JObject;
        }

        public void Reset(Instance ins)
        {
            foreach (Common.Task t in ins.Tasks)
                if (t is IntegrationTask task)
                {
                    task.Progress = 0;
                    task.StartTick = 0;
                    task.Status = DataStates.Idle;
                    task.Total = 0;
                    task.Table.Progress = 0;
                }
        }

        public void SaveSample(Database source, Database dest, List<Table> tables, string param, string path,
            string file)
        {
            // 表配置
            JArray tableArray = new JArray();

            foreach (Table t in tables)
            {
                JObject obj = new JObject()
                {
                    ["name"] = t.SourceName.Equals(t.DestName) ? t.SourceName : $"{t.SourceName},{t.DestName}",
                    ["order"] = t.Order,
                    ["orderSQL"] = t.OrderSQL,
                    ["whereSQL"] = t.SourceWhereSQL,
                    ["pageSize"] = t.PageSize,
                    ["mode"] = t.WriteMode == WriteModes.Append ? "Append" : "Update",
                    ["keyFields"] = t.KeyFields == null ? "" : string.Join(",", t.KeyFields),
                    ["skipFields"] = t.SkipFields == null ? "" : string.Join(",", t.SkipFields),
                    ["filter"] = t.Filter
                };

                if (!t.SourceWhereSQL.Equals(t.DestWhereSQL))
                    obj["destWhereSQL"] = t.DestWhereSQL;

                tableArray.Add(obj);
            }

            JObject tableData = new JObject()
            {
                ["params"] = param,
                ["tables"] = tableArray
            };

            WriteFile($"{path}Table-{file}", JsonConvert.SerializeObject(tableData, Formatting.Indented));

            // 任务配置
            JObject srcDB = new JObject()
            {
                ["dbms"] = source.DBMS,
                ["server"] = source.Server,
                ["port"] = source.Port,
                ["db"] = source.DB,
                ["user"] = source.User,
                ["password"] = source.Pwd,
                ["charset"] = source.CharSet
            };
            JArray sourceArray = new JArray() { srcDB };
            JObject dstDB = new JObject()
            {
                ["dbms"] = dest.DBMS,
                ["server"] = dest.Server,
                ["port"] = dest.Port,
                ["db"] = dest.DB,
                ["user"] = dest.User,
                ["password"] = dest.Pwd,
                ["charset"] = dest.CharSet
            };
            JObject instance = new JObject()
            {
                ["sources"] = sourceArray,
                ["dest"] = dstDB,
                ["tables"] = $"Table-{file}",
                ["readPages"] = 10,
                ["threads"] = 1
            };
            JArray insArray = new JArray() { instance };

            JObject profileData = new JObject()
            {
                ["instances"] = insArray,
                ["mode"] = "Once",
                ["runner"] = "Integration",
                ["runtime"] = "00:00",
                ["threads"] = 1
            };

            WriteFile($"{path}Profile-{file}", JsonConvert.SerializeObject(profileData, Formatting.Indented));
        }

        private void WriteFile(string file, string content)
        {
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Create))
                {
                    using (StreamWriter writer = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        writer.Write(content);
                        writer.Flush();
                    }
                }
            }
            catch (Exception) { }
        }
    }
}

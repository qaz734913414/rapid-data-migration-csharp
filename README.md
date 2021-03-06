# 快速数据迁移 v1.4
这是一个将数据从一个数据库快速迁移到另一个数据库的工具项目，它主打的特点是“快”！

## 一、为什么“快”？
要解释这个问题，首先要说说数据迁移是什么一回事。

数据迁移通常是这样子的：

![](https://ebuy.ucoz.com/rdm/seq1.png)

如果通用些，可能就类似这个样子了：

![](https://ebuy.ucoz.com/rdm/seq2.png)

这是一个串行处理的过程，因此，提取数据的时候，目标库空闲着，写入数据的时候，源库空闲着，处理能力白白浪费了。

可见，只要将这个串行处理改为并行，数据迁移效率即可大大提高。改动后类似这个样子：

![](https://ebuy.ucoz.com/rdm/seq3.png)

在某些测试场景下，这项小小的改动，可以获得 30% 的迁移效率提升！

## 二、制约迁移效率的其它主要因素
除了执行机制的制约以外，还有其它一些制约因素会明显影响迁移效率，本项目都有所考量。

### 1、网络传输速度
显然，如果只有一根小水管，算法层面优化能带来的提升空间就不大了，不过也不是什么事情都不能做，比如 MySQL 的客户端就支持压缩传输协议，只需一个连接串的配置项，就能用 CPU 运算能力换来一定的效率提升。

### 2、目标库写入效率
通常数据读取效率会高于写入，因此目标库的写入效率对整体效率而言相当关键。如果目标库客户端支持 Bulk Copy，使用批量复制接口，写入效率自然比其它方式更高。在某些测试场景下，批量复制比对数据脚本，效率可提升 20 倍！

这是优先选择 C# 实现的原因。

### 3、迁移顺序
多表并行迁移通常比单表串行迁移效率高，在这个前提下，合理安排迁移顺序，让更多时间处于并行状态，可提升整体效率。

## 三、其它重要特性
除了主打的效率特性外，本项目还有一些有意思的特性。

### 1、接口化
定义了一系列的功能接口，只需实现相应接口，即可实现扩展功能。主要接口如下：

| 接口 | 功能说明 |
| ---- | -------- |
| IAssemblyLoader | 程序集加载器接口，用于识别动态加载程序集 |
| IDataFilter | 数据 filter 接口，用于源表和目标表非完全匹配的场景 |
| IDataWrapper | 数据封装接口，提供数据中间格式 |
| IDBMSAssistant | 数据库配置接口，提供配置支持 |
| IDBMSReader | 数据库 reader 接口 |
| IDBMSWriter | 数据库 writer 接口 |
| IRunnerAnalyzer | 执行器解析接口，用于解析配置 |
| IRunnerAssistant | 执行器配置接口，提供配置支持 |
| IRunnerExecutor | 执行器执行接口，执行数据任务 |

### 2、支持超大数据集
简单实现，分批读取、写入而已。

### 3、配置助手
目前只是一个简单粗暴的实现，对帮助了解配置文件格式还是不错的，而且它实现了一个很不错的功能：解决表之间的外键依赖关系。关系数据库迁移数据，最麻烦之处是表与表之间可能存在外键约束，导致对迁移顺序有要求，配置助手能分析出这个先后依赖关系来。

### 4、异步日志
一个小巧的异步文件日志。

### 5、支持的数据库

| 数据库 | 测试版本 | 读 | 写 | 配置 |
| ------ | -------- | :--: | :--: | :--: |
| DB2 | 11.5 | √ | √ | √ |
| MongoDB | 3.6.3 | √ | √ | √ |
| Microsoft SQL Server | 13.0(2016) | √ | √ | √ |
| MySQL | 5.7.29 | √ | √ | √ |
| Oracle | 11g | √ | √ | √ |
| PostgreSQL | 10.12 | √ | √ | √ |
| Redis | 4.0.9 | √ | √ | √ |

## 四、开发环境

| 工具 | 版本 |
| ---- | ---- |
| Microsoft Visual Studio | Community 2019 |
| .NET Framework | 4.8 |

## 五、版本历史

| 版本 | 发布日期 | 主要特性 |
| ---- | ---- | ---- |
| 1.0 | 2020/4/2 | 全接口化，支持迁移和汇集模式，支持 MSSQL、MySQL和PostgreSQL |
| 1.1 | 2020/4/14 | 优化并行任务调度算法  |
| 1.2 | 2020/5/25 | 增加脱敏模式  |
| 1.3 | 2020/6/16 | 支持 Oracle, DB2，支持 schema，超时控制  |
| 1.4 | 2020/7/7 | 支持 MongoDB, Redis  |

## 六、感谢
感谢我们的老朋友 [Thomas](https://github.com/gztomash) 贡献了大部分实现代码。

## 写在最后
这是一个实验性项目，实现了一种解决思路，虽然完成度颇高，应用须谨慎。

J&H Workshop 成立于 2009 年春天，致力于小思维、小工具。

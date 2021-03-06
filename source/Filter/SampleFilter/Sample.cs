﻿using JHWork.DataMigration.Common;

namespace JHWork.DataMigration.Filter.Sample
{
    /// <summary>
    /// 数据过滤器示例
    /// </summary>
    public class SampleFilter : IAssemblyLoader, IDataFilter
    {
        public string GetName()
        {
            return "Sample";
        }

        public object GetValue(IDataWrapper data, int fieldIndex, string fieldName)
        {
            switch (fieldName.ToLower())
            {
                default:
                    return data.GetValue(fieldIndex);
            }
        }
    }
}

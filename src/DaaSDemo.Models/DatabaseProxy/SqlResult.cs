using System.Collections.Generic;
using Newtonsoft.Json;

namespace DaaSDemo.Models.DatabaseProxy
{
    /// <summary>
    ///     Response body when executing T-SQL.
    /// </summary>
    public class SqlResult
    {
        /// <summary>
        ///     Was the T-SQL successfully executed?
        /// </summary>
        public bool Success => Errors.Count == 0;

        /// <summary>
        ///     The query's result code (usually the number of rows affected).
        /// </summary>
        public int ResultCode { get; set; }

        /// <summary>
        ///     The T-SQL to execute.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Reuse)]
        public List<string> Messages { get; } = new List<string>();

        /// <summary>
        ///     Errors (if any) encountered while executing the T-SQL.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Reuse)]
        public List<SqlError> Errors { get; } = new List<SqlError>();
    }
}

using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace DaaSDemo.KubeClient.Models
{
    /// <summary>
    ///     APIServiceList is a list of APIService objects.
    /// </summary>
    public class APIServiceListV1Beta1 : KubeResourceListV1
    {
        /// <summary>
        ///     Description not provided.
        /// </summary>
        [JsonProperty("items", NullValueHandling = NullValueHandling.Ignore)]
        public List<APIServiceV1Beta1> Items { get; set; } = new List<APIServiceV1Beta1>();
    }
}

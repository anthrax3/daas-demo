using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace DaaSDemo.Models.Data
{
    /// <summary>
    ///     Represents a database tenant.
    /// </summary>
    [EntitySet("Tenant")]
    public class Tenant
    {
        /// <summary>
        ///     The tenant Id.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        ///     The tenant name.
        /// </summary>
        [MaxLength(200)]
        [Required(AllowEmptyStrings = false)]
        public string Name { get; set; }

        /// <summary>
        ///     Create a deep clone of the <see cref="Tenant"/>.
        /// </summary>
        /// <returns>
        ///     The cloned <see cref="Tenant"/>.
        /// </returns>
        public Tenant Clone()
        {
            return new Tenant
            {
                Id = Id,
                
                Name = Name
            };
        }
    }
}

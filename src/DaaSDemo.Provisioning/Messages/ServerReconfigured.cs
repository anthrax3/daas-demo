using System;

namespace DaaSDemo.Provisioning.Messages
{
    using Models.Data;

    /// <summary>
    ///     Message indicating that a database server has been reconfigured.
    /// </summary>
    public class ServerReconfigured
            : ServerStatusChanged
    {
        /// <summary>
        ///     Create a new <see cref="ServerReconfigured"/> message.
        /// </summary>
        /// <param name="serverId">
        ///     The Id of the server that was reconfigured.
        /// </param>
        public ServerReconfigured(string serverId)
            : base(serverId, ProvisioningStatus.Ready)
        {
        }
    }
}

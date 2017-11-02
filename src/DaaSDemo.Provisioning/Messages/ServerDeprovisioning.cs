namespace DaaSDemo.Provisioning.Messages
{
    using Data.Models;

    /// <summary>
    ///     Message indicating that a database server is being de-provisioned.
    /// </summary>
    public class ServerDeprovisioning
            : ServerStatusChanged
    {
        /// <summary>
        ///     Create a new <see cref="ServerDeprovisioning"/> message.
        /// </summary>
        /// <param name="serverId">
        ///     The Id of the server being de-provisioned.
        /// </param>
        public ServerDeprovisioning(int serverId)
            : base(serverId, ProvisioningStatus.Deprovisioning)
        {
        }
    }
}
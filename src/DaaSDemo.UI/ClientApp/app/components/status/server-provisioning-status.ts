import { bindable } from 'aurelia-framework';
import { Server, ProvisioningAction, ProvisioningStatus } from '../api/daas-api';

export class ServerProvisioningStatus {
    @bindable public server: Server;

    constructor() {}

    public get isReady(): boolean {
        return this.server && this.server.status == ProvisioningStatus.Ready;
    }

    public get isActionInProgress(): boolean {
        return this.server && this.server.action !== ProvisioningAction.None;
    }

    public get hasError(): boolean {
        return this.server && this.server.status === ProvisioningStatus.Error;
    }
}

/// <reference types="semantic-ui" />

import { bindable, inject, computedFrom } from 'aurelia-framework';
import * as $ from 'jquery';
import 'semantic';

import { Server, ServerProvisioningPhase } from '../api/daas-api';

export class ServerProvisioningPhaseProgress {
    @bindable private progressBarElement: Element
    
    @bindable public server: Server | null;

    /**
     * Called when the server has been updated.
     * 
     * @param oldValue The old value of the "server" field.
     * @param newValue The new value of the "server" field.
     */
    public serverChanged(oldValue: Server, newValue: Server): void {
        this.update();
    }

    /**
     * The server's current provisioning phase.
     */
    @computedFrom('server')    
    public get currentPhase(): ServerProvisioningPhase {
        if (!this.server)
            return ServerProvisioningPhase.None;

        return this.server.phase;
    }

    /**
     * If an action is in progress for the server, its percentage completion.
     */
    @computedFrom('server')
    public get actionPercentComplete(): number {
        switch (this.currentPhase) {
            case ServerProvisioningPhase.Instance:
                return 25;
            case ServerProvisioningPhase.Network:
                return 50;
            case ServerProvisioningPhase.Configuration:
                return 75;
            case ServerProvisioningPhase.Ingress:
                return 100;
            default:
                return 0;
        }
    }

    /**
     * Update the progress bar.
     */
    public update(): void {
        $(this.progressBarElement).progress('set percent', this.actionPercentComplete);
    }

    /**
     * Called when the component is attached to the DOM.
     */
    private attached(): void {
        $(this.progressBarElement).progress('set percent', this.actionPercentComplete);
    }

    /**
     * Called when the component is detached from the DOM.
     */
    private detached(): void {
        $(this.progressBarElement).progress('destroy');
    }
}
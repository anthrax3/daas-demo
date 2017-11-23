/// <reference types="semantic-ui" />

import { bindable } from 'aurelia-framework';
import * as $ from 'jquery';
import 'semantic';

import { Server } from '../../api/daas-api';

const noAction = () => {};

export class ServerActionsMenu {
    @bindable private rootElement: Element | null = null;
    
    @bindable public server: Server | null = null;
    @bindable public repairClicked: () => void = noAction;
    @bindable public destroyClicked: () => void = noAction;

    constructor() {}

    public attached(): void {
        if (this.rootElement) {
            $(this.rootElement).dropdown('setting', 'keepOnScreen', true);
        }
    }

    public detached(): void {
        if (this.rootElement) {
            $(this.rootElement).dropdown('destroy');
        }
    }

    public get canRepair(): boolean {
        if (!this.server)
            return false;

        switch (this.server.status) {
            case 'Ready':
            case 'Error':
                return true;
            default:
                return false;
        }
    }

    public get canDestroy(): boolean {
        if (!this.server)
            return false;

        switch (this.server.status) {
            case 'Ready':
            case 'Error':
                return true;
            default:
                return false;
        }
    }
}

import { inject, computedFrom, bindable } from 'aurelia-framework';
import { NewInstance } from 'aurelia-dependency-injection';
import { RouteConfig } from 'aurelia-router';
import { ValidationRules, ValidationController } from 'aurelia-validation';

import { DaaSAPI } from '../../../services/api/daas-api';

@inject(DaaSAPI, NewInstance.of(ValidationController))
export class NewUserForm {
    @bindable public newUser: NewUser | null = null;

    @bindable public createClicked: (newUser: NewUser) => void;
    @bindable public cancelClicked: () => void;

    /**
     * Create a new user user creation view model.
     * 
     * @param api The DaaS API client.
     * @param validationController The validation controller for the current context.
     */
    constructor(private api: DaaSAPI, public validationController: ValidationController) {
        this.createClicked = (newUser) => {};
        this.cancelClicked = () => {};
    }
}

/**
 * Represents the form values for creating a user.
 */
export class NewUser {
    public displayName: string | null = null;
    public email: string | null = null;
    public password: string | null = null;
    public passwordConfirmation: string | null = null;
    public isAdmin: boolean = false;
    
    public get passwordsMatch(): boolean {
        if (!this.password) {
            return true; // Don't complain about password mismatch until they've actually entered one.
        }

        return this.password === this.passwordConfirmation;
    }
}

ValidationRules
    .ensure<NewUser, string>('displayName').displayName('Name')
        .required()
        .minLength(5)
    .ensure('email').displayName('Email address')
        .required()
        .email()
    .ensure('password').displayName('Password')
        .required()
        .minLength(5)
    .ensure('passwordConfirmation').displayName('Password confirmation')
        .required()
        .minLength(5)
        .satisfies(
            (_, newUser: NewUser) => newUser.passwordsMatch
        ).withMessage('Passwords must match')
    .on(NewUser);
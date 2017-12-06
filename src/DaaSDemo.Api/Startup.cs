﻿using IdentityModel;
using IdentityServer4.AccessTokenValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Converters;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DaaSDemo.Api
{
    using Common.Options;
    using Data;
    using Identity;
    using Models.Data;

    /// <summary>
    ///     Startup logic for the Database-as-a-Service demo API.
    /// </summary>
    public class Startup
    {
        /// <summary>
        ///     Create a new <see cref="Startup"/>.
        /// </summary>
        /// <param name="configuration">
        ///     The application configuration.
        /// </param>
        public Startup(IConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            Configuration = configuration;
            CorsOptions = CorsOptions.From(Configuration);
            SecurityOptions = SecurityOptions.From(Configuration);
            DatabaseOptions = DatabaseOptions.From(Configuration);
        }

        /// <summary>
        ///     The application configuration.
        /// </summary>
        public IConfiguration Configuration { get; }

        /// <summary>
        ///     CORS options for the application.
        /// </summary>
        public CorsOptions CorsOptions { get; }

        /// <summary>
        ///     Security options for the application.
        /// </summary>
        public SecurityOptions SecurityOptions { get; }

        /// <summary>
        ///     Database options for the application.
        /// </summary>
        public DatabaseOptions DatabaseOptions { get; }

        /// <summary>
        ///     Configure application services.
        /// </summary>
        /// <param name="services">
        ///     The application service collection.
        /// </param>
        public void ConfigureServices(IServiceCollection services)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));

            services.AddOptions();
            services.AddDaaSOptions(Configuration);

            services.AddDaaSDataAccess();

            services
                .AddIdentity<AppUser, AppRole>(identity =>
                {
                    identity.ClaimsIdentity.UserIdClaimType = JwtClaimTypes.Subject;
                    identity.ClaimsIdentity.UserNameClaimType = JwtClaimTypes.Name;
                    identity.ClaimsIdentity.RoleClaimType = JwtClaimTypes.Role;
                })
                .AddDaaSIdentityStores();

            services.AddAuthorization(authorization =>
            {
                AuthorizationPolicy defaultPolicy =
                    new AuthorizationPolicyBuilder(IdentityServerAuthenticationDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser()
                        .RequireRole("User")
                        .Build();

                authorization.DefaultPolicy = defaultPolicy;
                authorization.AddPolicy("Default", authorization.DefaultPolicy);
                authorization.AddPolicy("User", authorization.DefaultPolicy);

                authorization.AddPolicy("Administrator",
                    new AuthorizationPolicyBuilder(IdentityServerAuthenticationDefaults.AuthenticationScheme)
                        .RequireAuthenticatedUser()
                        .RequireRole("Administrator")
                        .Build()
                );
            });

            services.AddAuthentication(IdentityServerAuthenticationDefaults.AuthenticationScheme)
                .AddIdentityServerAuthentication(options =>
                {
                    options.Authority = SecurityOptions.Authority;
                    options.RequireHttpsMetadata = false;

                    options.ApiName = "daas-api-v1";
                    options.ApiSecret = "secret".ToSha256();
                    
                    options.EnableCaching = true;
                    options.CacheDuration = TimeSpan.FromMinutes(1);
                });

            services.AddCors(cors =>
            {
                if (String.IsNullOrWhiteSpace(CorsOptions.UI))
                    throw new InvalidOperationException("Application configuration is missing CORS base address for UI.");

                // Allow requests from the UI.                
                string[] uiBaseAddresses = CorsOptions.UI.Split(';');
                Log.Information("CORS enabled for UI: {@BaseAddresses}", uiBaseAddresses);

                cors.AddPolicy("UI", policy =>
                    policy.WithOrigins(uiBaseAddresses)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                );
            });

            services.AddMvc()
                .AddJsonOptions(json =>
                {
                    json.SerializerSettings.Converters.Add(
                        new StringEnumConverter()
                    );
                });
            services.AddDataProtection(dataProtection =>
            {
                dataProtection.ApplicationDiscriminator = "DaaS.Demo";
            });
        }

        /// <summary>
        ///     Configure the application pipeline.
        /// </summary>
        /// <param name="app">
        ///     The application pipeline builder.
        /// </param>
        public void Configure(IApplicationBuilder app, IApplicationLifetime appLifetime)
        {
            app.UseDeveloperExceptionPage();
            app.UseCors("UI");
            app.UseAuthentication();
            app.UseMvc();

            appLifetime.ApplicationStopped.Register(Serilog.Log.CloseAndFlush);
        }
    }
}

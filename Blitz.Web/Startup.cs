using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Blitz.Web.Auth;
using Blitz.Web.Cronjobs;
using Blitz.Web.Hangfire;
using Blitz.Web.Http;
using Blitz.Web.Identity;
using Blitz.Web.Maintenance;
using Blitz.Web.Persistence;
using Hangfire;
using Hangfire.EntityFrameworkCore;
using IdentityModel;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi.Models;
using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace Blitz.Web
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IHostEnvironment environment)
        {
            Configuration = configuration;
            Environment = environment;
        }

        public IConfiguration Configuration { get; }
        public IHostEnvironment Environment { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddTransient<ICronjobTriggerer, HangfireCronjobTriggerer>();
            services.AddTransient<ICronjobRegistrationService, HangfireCronjobRegistrationService>();
            services.AddGarbageCollector();
            services.AddHttpClient<HttpRequestJob>(
                (provider, client) => { client.Timeout = TimeSpan.FromSeconds(20); }
            );

            // services.AddTransient<IdentitySeeder>();
            services.AddAutoMapper(typeof(Startup).Assembly);
            services.AddDbContext<BlitzDbContext>(builder =>
            {
                builder = builder
                    .EnableDetailedErrors(Environment.IsDevelopment())
                    .EnableSensitiveDataLogging(Environment.IsDevelopment());
                if (Configuration.GetConnectionString("BlitzPostgres") is { } postgresDsn)
                {
                    builder = builder.UseNpgsql(
                        postgresDsn,
                        pg => pg.MigrationsHistoryTable("__ef_migrations")
                    );
                }
            });
            services.AddTransient<IdentitySeeder>();

            services.AddRouting(o => o.LowercaseUrls = true);
            services.AddControllers(options => options.Filters.Add<MappingExceptionFilter>());
            services.AddSwaggerGen(
                options =>
                {
                    const string scope = "api";
                    options.CustomOperationIds(e => $"{e.ActionDescriptor.RouteValues["action"]}");
                    options.SwaggerDoc("v1", new OpenApiInfo {Title = "Blitz", Version = "v1"});
                    options.OperationFilter<PopulateMethodMetadataOperationFilter>();
                    options.AddSecurityDefinition("oidc", new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.OAuth2,
                        In = ParameterLocation.Header,
                        Name = HeaderNames.Authorization,
                        Flows = new OpenApiOAuthFlows
                        {
                            AuthorizationCode = new OpenApiOAuthFlow
                            {
                                AuthorizationUrl = new Uri("https://localhost:5001/connect/authorize"),
                                TokenUrl = new Uri("https://localhost:5001/connect/token"),
                                Scopes = new Dictionary<string, string>
                                {
                                    [scope] = "read + write",
                                },
                            },
                        },
                    });
                    options.AddSecurityRequirement(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = "oidc",
                            },
                        }] = new[] {scope}
                    });
                }
            );

            services.AddHangfire((provider, configuration) =>
                {
                    configuration.UseFilter(new AutomaticRetryAttribute {Attempts = 1});
                    configuration.UseInMemoryStorage();
                    // configuration.UseEFCoreStorage(() => provider.CreateScope().ServiceProvider.GetRequiredService<BlitzDbContext>(),
                    //     new EFCoreStorageOptions());
                }
            );
            services.AddHangfireServer(options => options.ServerName = Environment.ApplicationName);

            services.AddOpenIddict()
                .AddServer(builder =>
                {
                    builder.EnableDegradedMode();
                    builder
                        .SetAuthorizationEndpointUris("/connect/authorize")
                        .SetTokenEndpointUris("/connect/token")
                        .SetUserinfoEndpointUris("/connect/userinfo");

                    builder.UseAspNetCore()
                        // .EnableTokenEndpointPassthrough()
                        // .EnableAuthorizationEndpointPassthrough()
                        .EnableUserinfoEndpointPassthrough();

                    builder
                        .AddEphemeralEncryptionKey()
                        .AddEphemeralSigningKey()
                        .DisableAccessTokenEncryption();

                    builder
                        .AllowAuthorizationCodeFlow()
                        .AllowClientCredentialsFlow()
                        .AllowRefreshTokenFlow();

                    // Force client applications to use Proof Key for Code Exchange (PKCE).
                    builder.RequireProofKeyForCodeExchange();

                    builder.RegisterScopes(OpenIddictConstants.Scopes.Email, OpenIddictConstants.Scopes.Profile, "api");

                    builder.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(
                        validationBuilder =>
                        {
                            validationBuilder.UseInlineHandler(context =>
                            {
                                /*if (!string.Equals(context.ClientId, "ui", StringComparison.Ordinal))
                                {
                                    context.Reject(error: OpenIddictConstants.Errors.InvalidClient,
                                        description: "Specified client id is not registered");
                                    return ValueTask.CompletedTask;
                                }*/

                                // if (!string.Equals(context.RedirectUri, "http://localhost:7890/", StringComparison.Ordinal))
                                // {
                                // }

                                return ValueTask.CompletedTask;
                            });
                        });
                    builder.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(validationBuilder =>
                    {
                        validationBuilder.UseInlineHandler(context =>
                        {
                            /*if (!string.Equals(context.ClientId, "ui", StringComparison.Ordinal))
                            {
                                context.Reject(error: OpenIddictConstants.Errors.InvalidClient, description: "Specified client id is not registered");
                            }*/

                            return ValueTask.CompletedTask;
                        });
                    });
                    builder.AddEventHandler<OpenIddictServerEvents.HandleAuthorizationRequestContext>(reqBuilder =>
                    {
                        reqBuilder.UseInlineHandler(async context =>
                        {
                            var request = context.Transaction.GetHttpRequest() ??
                                          throw new InvalidOperationException("The ASP.NET Core request cannot be retrieved.");
                            var principal = (await request.HttpContext.AuthenticateAsync(AppAuthenticationDefaults.AuthenticationScheme)).Principal;
                            if (principal == null)
                            {
                                await request.HttpContext.ChallengeAsync(AppAuthenticationDefaults.AuthenticationScheme);
                                context.HandleRequest();
                                return;
                            }


                            var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType);
                            identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, principal.GetClaim(ClaimTypes.NameIdentifier)));
                            identity.AddClaim(new Claim(OpenIddictConstants.Claims.Name, principal.GetClaim(ClaimTypes.Name)));
                            foreach (var claim in identity.Claims)
                            {
                                claim.SetDestinations(OpenIddictConstants.Destinations.AccessToken);
                                claim.SetDestinations(OpenIddictConstants.Destinations.IdentityToken);
                            }

                            principal = new ClaimsPrincipal(identity);
                            principal.SetScopes(context.Request.GetScopes());

                            context.Principal = principal;
                        });
                    });
                }).AddValidation(builder =>
                {
                    builder.UseAspNetCore();
                    builder.UseLocalServer();
                });


            services.AddTransient<IExternalUserImporter, ThyExternalUserImporter>();
            services.AddTransient<IClaimsTransformation, LoadAuthorizationClaimsTransformer>();
            services.AddAuthentication(options => { options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme; })
                .AddCookie()
                .AddThy(Configuration)
                .AddJwtBearer(options =>
                {
                    options.Authority = "https://localhost:5001";
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateAudience = false,
                    };
                });


            services.AddScoped<IAuthorizationHandler, ProjectManagerRequirement>();
            services.AddAuthorization(options =>
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddAuthenticationSchemes(
                        JwtBearerDefaults.AuthenticationScheme
                    )
                    .Build();

                options.AddPolicy(AuthorizationPolicies.RequireProjectManager, AuthorizationPolicies.RequireProjectManagerPolicy);
                options.AddPolicy(AuthorizationPolicies.RequireAdmin, AuthorizationPolicies.RequireAdminPolicy);
            });


            services.AddHttpContextAccessor();
            services.AddSpaStaticFiles(options => { options.RootPath = "ClientApp/build"; });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, BlitzDbContext dbContext)
        {
            app
                .InitCronjobs()
                .InitGarbageCollector();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseForwardedHeaders(new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.All
            });
            app.UseCors(builder => builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
            app.UseStaticFiles();

            // app.UseHttpsRedirection();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseSwaggerUI(
                c =>
                {
                    c.DocumentTitle = "Blitz API";
                    c.DisplayOperationId();
                    c.RoutePrefix = "api";
                    c.SwaggerEndpoint("/openapi/v1.json", "Blitz API");
                    c.OAuthConfigObject.Scopes = new[] {"api"};
                    c.OAuthConfigObject.ClientId = "demoapp";
                    c.OAuthUsePkce();
                }
            );

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapSwagger("/openapi/{documentName}.json");
                endpoints.MapControllers();

                if (!Environment.IsDevelopment())
                {
                    endpoints.MapFallbackToFile("index.html");
                }
            });

            // if (Environment.IsDevelopment())
            // {
            //     app.UseSpa(spa =>
            //     {
            //         spa.Options.SourcePath = "ClientApp";
            //         spa.UseProxyToSpaDevelopmentServer("http://localhost:5002");
            //     });
            // }
        }
    }
}
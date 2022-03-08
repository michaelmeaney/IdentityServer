// Copyright (c) Duende Software. All rights reserved.
// See LICENSE in the project root for license information.


using System.Linq;
using System.Threading.Tasks;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Stores;
using Duende.IdentityServer.Test;
using FluentAssertions;
using IntegrationTests.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IdentityModel.Client;
using System.Collections.Generic;

namespace IntegrationTests.Hosting;

public class ServerSideSessionTests
{
    private const string Category = "Server Side Sessions";

    private IdentityServerPipeline _pipeline = new IdentityServerPipeline();
    private IServerSideSessionStore _sessionStore;
    private IServerSideTicketStore _ticketStore;
    private ISessionManagementService _sessionMgmt;
    private IPersistedGrantStore _grantStore;
    private IRefreshTokenStore _refreshTokenStore;

    private MockServerUrls _urls = new MockServerUrls();

    public class MockServerUrls : IServerUrls
    {
        public string Origin { get; set; }
        public string BasePath { get; set; }
    }
    
    public ServerSideSessionTests()
    {
        _urls.Origin = IdentityServerPipeline.BaseUrl;
        _urls.BasePath = "/";
        _pipeline.OnPostConfigureServices += s => 
        {
            s.AddSingleton<IServerUrls>(_urls);
            s.AddIdentityServerBuilder().AddServerSideSessions();
        };
        _pipeline.OnPostConfigure += app =>
        {
            app.Map("/user", ep => {
                ep.Run(ctx => 
                { 
                    if (ctx.User.Identity.IsAuthenticated)
                    {
                        ctx.Response.StatusCode = 200;
                    }
                    else
                    {
                        ctx.Response.StatusCode = 401;
                    }
                    return Task.CompletedTask;
                });
            });
        };


        _pipeline.Users.Add(new TestUser
        {
            SubjectId = "bob",
            Username = "bob",
        });
        _pipeline.Users.Add(new TestUser
        {
            SubjectId = "alice",
            Username = "alice",
        });

        _pipeline.Clients.Add(new Client
        {
            ClientId = "client",
            AllowedGrantTypes = GrantTypes.Code,
            RequireClientSecret = false,
            RequireConsent = false,
            RequirePkce = false,
            BackChannelLogoutSessionRequired = false,
            AllowedScopes = { "openid", "api" },
            AllowOfflineAccess = true,
            RedirectUris = { "https://client/callback" },
            BackChannelLogoutUri = "https://client/bc-logout"
        });
        _pipeline.IdentityScopes.Add(new IdentityResources.OpenId());
        _pipeline.ApiScopes.Add(new ApiScope("api"));

        _pipeline.Initialize();

        _sessionStore = _pipeline.Resolve<IServerSideSessionStore>();
        _ticketStore = _pipeline.Resolve<IServerSideTicketStore>();
        _sessionMgmt = _pipeline.Resolve<ISessionManagementService>();
        _grantStore = _pipeline.Resolve<IPersistedGrantStore>();
        _refreshTokenStore = _pipeline.Resolve<IRefreshTokenStore>();
    }

    async Task<bool> IsLoggedIn()
    {
        var response = await _pipeline.BrowserClient.GetAsync(IdentityServerPipeline.BaseUrl + "/user");
        return response.StatusCode == System.Net.HttpStatusCode.OK;
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task login_should_create_server_side_session()
    {
        (await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "bob" })).Should().BeEmpty();
        await _pipeline.LoginAsync("bob");
        (await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "bob" })).Should().NotBeEmpty();
        (await IsLoggedIn()).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task remove_server_side_session_should_logout_user()
    {
        await _pipeline.LoginAsync("bob");

        await _sessionStore.DeleteSessionsAsync(new SessionFilter { SubjectId = "bob" });
        (await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "bob" })).Should().BeEmpty();

        (await IsLoggedIn()).Should().BeFalse();
    }
    
    [Fact]
    [Trait("Category", Category)]
    public async Task logout_should_remove_server_side_session()
    {
        await _pipeline.LoginAsync("bob");
        await _pipeline.LogoutAsync();

        (await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "bob" })).Should().BeEmpty();

        (await IsLoggedIn()).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task corrupted_server_side_session_should_logout_user()
    {
        await _pipeline.LoginAsync("bob");

        var sessions = await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "bob" });
        var session = await _sessionStore.GetSessionAsync(sessions.Single().Key);
        session.Ticket = "invalid";
        await _sessionStore.UpdateSessionAsync(session);

        (await IsLoggedIn()).Should().BeFalse();
        (await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "bob" })).Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task subsequent_logins_should_update_server_side_session()
    {
        await _pipeline.LoginAsync("bob");

        var key = (await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "bob" })).Single().Key;

        await _pipeline.LoginAsync("bob");

        (await IsLoggedIn()).Should().BeTrue();
        var sessions = await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "bob" });
        sessions.First().Key.Should().Be(key);
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task changing_users_should_create_new_server_side_session()
    {
        await _pipeline.LoginAsync("bob");

        var bob_session = (await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "bob" })).Single();

        await Task.Delay(1000);
        await _pipeline.LoginAsync("alice");

        (await IsLoggedIn()).Should().BeTrue();
        var alice_session = (await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "alice" })).Single();

        alice_session.Key.Should().Be(bob_session.Key);
        (alice_session.Created > bob_session.Created).Should().BeTrue();
        alice_session.SessionId.Should().NotBe(bob_session.SessionId);
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task getsessions_on_ticket_store_should_use_session_store()
    {
        await _pipeline.LoginAsync("alice");
        _pipeline.RemoveLoginCookie();
        await _pipeline.LoginAsync("alice");
        _pipeline.RemoveLoginCookie();
        await _pipeline.LoginAsync("alice");
        _pipeline.RemoveLoginCookie();

        var tickets = await _ticketStore.GetSessionsAsync(new SessionFilter { SubjectId = "alice" });
        var sessions = await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "alice" });

        tickets.Select(x => x.SessionId).Should().BeEquivalentTo(sessions.Select(x => x.SessionId));
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task querysessions_on_ticket_store_should_use_session_store()
    {
        await _pipeline.LoginAsync("alice");
        _pipeline.RemoveLoginCookie();
        await _pipeline.LoginAsync("alice");
        _pipeline.RemoveLoginCookie();
        await _pipeline.LoginAsync("alice");
        _pipeline.RemoveLoginCookie();

        var tickets = await _ticketStore.QuerySessionsAsync(new SessionQuery { SubjectId = "alice" });
        var sessions = await _sessionStore.QuerySessionsAsync(new SessionQuery { SubjectId = "alice" });

        tickets.ResultsToken.Should().Be(sessions.ResultsToken);
        tickets.HasPrevResults.Should().Be(sessions.HasPrevResults);
        tickets.HasNextResults.Should().Be(sessions.HasNextResults);
        tickets.TotalCount.Should().Be(sessions.TotalCount);
        tickets.TotalPages.Should().Be(sessions.TotalPages);
        tickets.CurrentPage.Should().Be(sessions.CurrentPage);

        tickets.Results.Select(x => x.SessionId).Should().BeEquivalentTo(sessions.Results.Select(x => x.SessionId));
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task querysessions_on_session_mgmt_service_should_use_ticket_store()
    {
        await _pipeline.LoginAsync("alice");
        _pipeline.RemoveLoginCookie();
        await _pipeline.LoginAsync("alice");
        _pipeline.RemoveLoginCookie();
        await _pipeline.LoginAsync("alice");
        _pipeline.RemoveLoginCookie();

        var sessions = await _sessionMgmt.QuerySessionsAsync(new SessionQuery { SubjectId = "alice" });
        var tickets = await _ticketStore.QuerySessionsAsync(new SessionQuery { SubjectId = "alice" });

        tickets.ResultsToken.Should().Be(sessions.ResultsToken);
        tickets.HasPrevResults.Should().Be(sessions.HasPrevResults);
        tickets.HasNextResults.Should().Be(sessions.HasNextResults);
        tickets.TotalCount.Should().Be(sessions.TotalCount);
        tickets.TotalPages.Should().Be(sessions.TotalPages);
        tickets.CurrentPage.Should().Be(sessions.CurrentPage);

        tickets.Results.Select(x => x.SessionId).Should().BeEquivalentTo(sessions.Results.Select(x => x.SessionId));
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task remove_sessions_should_delete_refresh_tokens()
    {
        await _pipeline.LoginAsync("alice");

        var authzResponse = await _pipeline.RequestAuthorizationEndpointAsync("client", "code", "openid api offline_access", "https://client/callback");
        var tokenResponse = await _pipeline.BackChannelClient.RequestAuthorizationCodeTokenAsync(new AuthorizationCodeTokenRequest
        {
            Address = IdentityServerPipeline.TokenEndpoint,
            ClientId = "client",
            Code = authzResponse.Code,
            RedirectUri = "https://client/callback"
        });

        (await _grantStore.GetAllAsync(new PersistedGrantFilter { SubjectId = "alice" })).Should().NotBeEmpty();

        await _sessionMgmt.RemoveSessionsAsync(new RemoveSessionsContext
        {
            SubjectId = "alice",
            RemoveServerSideSession = false,
            RevokeConsents = false,
            RevokeTokens = true,
            SendBackchannelLogoutNotification = false
        });

        (await _grantStore.GetAllAsync(new PersistedGrantFilter { SubjectId = "alice" })).Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task remove_sessions_with_clientid_filter_should_filter_delete_refresh_tokens()
    {
        await _pipeline.LoginAsync("alice");

        var authzResponse = await _pipeline.RequestAuthorizationEndpointAsync("client", "code", "openid api offline_access", "https://client/callback");
        var tokenResponse = await _pipeline.BackChannelClient.RequestAuthorizationCodeTokenAsync(new AuthorizationCodeTokenRequest
        {
            Address = IdentityServerPipeline.TokenEndpoint,
            ClientId = "client",
            Code = authzResponse.Code,
            RedirectUri = "https://client/callback"
        });

        (await _grantStore.GetAllAsync(new PersistedGrantFilter { SubjectId = "alice" })).Should().NotBeEmpty();

        await _sessionMgmt.RemoveSessionsAsync(new RemoveSessionsContext
        {
            SubjectId = "alice",
            RemoveServerSideSession = false,
            RevokeConsents = false,
            RevokeTokens = true,
            SendBackchannelLogoutNotification = false,
            ClientIds = new[] { "foo" }
        });

        (await _grantStore.GetAllAsync(new PersistedGrantFilter { SubjectId = "alice" })).Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task remove_sessions_should_invoke_backchannel_logout()
    {
        await _pipeline.LoginAsync("alice");

        var authzResponse = await _pipeline.RequestAuthorizationEndpointAsync("client", "code", "openid api offline_access", "https://client/callback");
        var tokenResponse = await _pipeline.BackChannelClient.RequestAuthorizationCodeTokenAsync(new AuthorizationCodeTokenRequest
        {
            Address = IdentityServerPipeline.TokenEndpoint,
            ClientId = "client",
            Code = authzResponse.Code,
            RedirectUri = "https://client/callback"
        });

        _pipeline.BackChannelMessageHandler.InvokeWasCalled.Should().BeFalse();

        await _sessionMgmt.RemoveSessionsAsync(new RemoveSessionsContext
        {
            SubjectId = "alice",
            RemoveServerSideSession = false,
            RevokeConsents = false,
            RevokeTokens = false,
            SendBackchannelLogoutNotification = true
        });

        _pipeline.BackChannelMessageHandler.InvokeWasCalled.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task remove_sessions_with_clientid_filter_should_filter_backchannel_logout()
    {
        await _pipeline.LoginAsync("alice");

        var authzResponse = await _pipeline.RequestAuthorizationEndpointAsync("client", "code", "openid api offline_access", "https://client/callback");
        var tokenResponse = await _pipeline.BackChannelClient.RequestAuthorizationCodeTokenAsync(new AuthorizationCodeTokenRequest
        {
            Address = IdentityServerPipeline.TokenEndpoint,
            ClientId = "client",
            Code = authzResponse.Code,
            RedirectUri = "https://client/callback"
        });

        _pipeline.BackChannelMessageHandler.InvokeWasCalled.Should().BeFalse();

        await _sessionMgmt.RemoveSessionsAsync(new RemoveSessionsContext
        {
            SubjectId = "alice",
            RemoveServerSideSession = false,
            RevokeConsents = false,
            RevokeTokens = false,
            SendBackchannelLogoutNotification = true,
            ClientIds = new List<string>{ "foo" }
        });

        _pipeline.BackChannelMessageHandler.InvokeWasCalled.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", Category)]
    public async Task remove_sessions_should_remove_server_sessions()
    {
        await _pipeline.LoginAsync("alice");

        var authzResponse = await _pipeline.RequestAuthorizationEndpointAsync("client", "code", "openid api offline_access", "https://client/callback");
        var tokenResponse = await _pipeline.BackChannelClient.RequestAuthorizationCodeTokenAsync(new AuthorizationCodeTokenRequest
        {
            Address = IdentityServerPipeline.TokenEndpoint,
            ClientId = "client",
            Code = authzResponse.Code,
            RedirectUri = "https://client/callback"
        });

        _pipeline.BackChannelMessageHandler.InvokeWasCalled.Should().BeFalse();
        
        (await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "alice" })).Should().NotBeEmpty();

        await _sessionMgmt.RemoveSessionsAsync(new RemoveSessionsContext
        {
            SubjectId = "alice",
            RemoveServerSideSession = true,
            RevokeConsents = false,
            RevokeTokens = false,
            SendBackchannelLogoutNotification = false
        });

        (await _sessionStore.GetSessionsAsync(new SessionFilter { SubjectId = "alice" })).Should().BeEmpty();
    }
}

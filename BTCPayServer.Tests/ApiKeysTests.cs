using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Security.APIKeys;
using BTCPayServer.Tests.Logging;
using BTCPayServer.Views.Manage;
using Newtonsoft.Json;
using OpenQA.Selenium;
using Xunit;
using Xunit.Abstractions;

namespace BTCPayServer.Tests
{
    public class ApiKeysTests
    {
        public const int TestTimeout = TestUtils.TestTimeout;

        public const string TestApiPath = "api/test/apikey";
        public ApiKeysTests(ITestOutputHelper helper)
        {
            Logs.Tester = new XUnitLog(helper) { Name = "Tests" };
            Logs.LogProvider = new XUnitLogProvider(helper);
        }

        [Fact(Timeout = TestTimeout)]
        [Trait("Selenium", "Selenium")]
        public async Task CanCreateApiKeys()
        {
            //there are 2 ways to create api keys:
            //as a user through your profile
            //as an external application requesting an api key from a user

            using (var s = SeleniumTester.Create())
            {
                await s.StartAsync();
                var tester = s.Server;

                var user = tester.NewAccount();
                user.GrantAccess();
                await user.MakeAdmin(false);
                s.GoToLogin();
                s.Login(user.RegisterDetails.Email, user.RegisterDetails.Password);
                s.GoToProfile(ManageNavPages.APIKeys);
                s.Driver.FindElement(By.Id("AddApiKey")).Click();

                //not an admin, so this permission should not show
                Assert.DoesNotContain("btcpay.server.canmodifyserversettings", s.Driver.PageSource);
                await user.MakeAdmin();
                s.Logout();
                s.GoToLogin();
                s.Login(user.RegisterDetails.Email, user.RegisterDetails.Password);
                s.GoToProfile(ManageNavPages.APIKeys);
                s.Driver.FindElement(By.Id("AddApiKey")).Click();
                Assert.Contains("btcpay.server.canmodifyserversettings", s.Driver.PageSource);

                //server management should show now
                s.SetCheckbox(s, "btcpay.server.canmodifyserversettings", true);
                s.SetCheckbox(s, "btcpay.store.canmodifystoresettings", true);
                s.SetCheckbox(s, "btcpay.user.canviewprofile", true);
                s.Driver.FindElement(By.Id("Generate")).Click();
                var superApiKey = s.AssertHappyMessage().FindElement(By.TagName("code")).Text;

                //this api key has access to everything
                await TestApiAgainstAccessToken(superApiKey, tester, user, $"{Policies.CanModifyServerSettings};{Policies.CanModifyStoreSettings};{Policies.CanViewProfile}");


                s.Driver.FindElement(By.Id("AddApiKey")).Click();
                s.SetCheckbox(s, "btcpay.server.canmodifyserversettings", true);
                s.Driver.FindElement(By.Id("Generate")).Click();
                var serverOnlyApiKey = s.AssertHappyMessage().FindElement(By.TagName("code")).Text;
                await TestApiAgainstAccessToken(serverOnlyApiKey, tester, user,
                    Policies.CanModifyServerSettings);


                s.Driver.FindElement(By.Id("AddApiKey")).Click();
                s.SetCheckbox(s, "btcpay.store.canmodifystoresettings", true);
                s.Driver.FindElement(By.Id("Generate")).Click();
                var allStoreOnlyApiKey = s.AssertHappyMessage().FindElement(By.TagName("code")).Text;
                await TestApiAgainstAccessToken(allStoreOnlyApiKey, tester, user,
                    Policies.CanModifyStoreSettings);

                s.Driver.FindElement(By.Id("AddApiKey")).Click();
                s.Driver.FindElement(By.CssSelector("button[value=change-store-mode]")).Click();
                //there should be a store already by default in the dropdown
                var dropdown = s.Driver.FindElement(By.Name("SpecificStores[0]"));
                var option = dropdown.FindElement(By.TagName("option"));
                var storeId = option.GetAttribute("value");
                option.Click();
                s.Driver.FindElement(By.Id("Generate")).Click();
                var selectiveStoreApiKey = s.AssertHappyMessage().FindElement(By.TagName("code")).Text;
                await TestApiAgainstAccessToken(selectiveStoreApiKey, tester, user,
                    Permission.Create(Policies.CanModifyStoreSettings, storeId).ToString());

                s.Driver.FindElement(By.Id("AddApiKey")).Click();
                s.Driver.FindElement(By.Id("Generate")).Click();
                var noPermissionsApiKey = s.AssertHappyMessage().FindElement(By.TagName("code")).Text;
                await TestApiAgainstAccessToken(noPermissionsApiKey, tester, user, string.Empty);

                await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
                {
                    await TestApiAgainstAccessToken<bool>("incorrect key", $"{TestApiPath}/me/id",
                        tester.PayTester.HttpClient);
                });


                //let's test the authorized screen now
                //options for authorize are:
                //applicationName
                //redirect
                //permissions
                //strict
                //selectiveStores
                var authUrl = BTCPayServerClient.GenerateAuthorizeUri(tester.PayTester.ServerUri,
                    new[] { Policies.CanModifyStoreSettings, Policies.CanModifyServerSettings }).ToString();
                s.Driver.Navigate().GoToUrl(authUrl);
                s.Driver.PageSource.Contains("kukksappname");
                Assert.Equal("hidden", s.Driver.FindElement(By.Id("btcpay.store.canmodifystoresettings")).GetAttribute("type").ToLowerInvariant());
                Assert.Equal("true", s.Driver.FindElement(By.Id("btcpay.store.canmodifystoresettings")).GetAttribute("value").ToLowerInvariant());
                Assert.Equal("hidden", s.Driver.FindElement(By.Id("btcpay.server.canmodifyserversettings")).GetAttribute("type").ToLowerInvariant());
                Assert.Equal("true", s.Driver.FindElement(By.Id("btcpay.server.canmodifyserversettings")).GetAttribute("value").ToLowerInvariant());
                Assert.DoesNotContain("change-store-mode", s.Driver.PageSource);
                s.Driver.FindElement(By.Id("consent-yes")).Click();
                var url = s.Driver.Url;
                IEnumerable<KeyValuePair<string, string>> results = url.Split("?").Last().Split("&")
                    .Select(s1 => new KeyValuePair<string, string>(s1.Split("=")[0], s1.Split("=")[1]));

                var apiKeyRepo = s.Server.PayTester.GetService<APIKeyRepository>();

                await TestApiAgainstAccessToken(results.Single(pair => pair.Key == "key").Value, tester, user,
                    (await apiKeyRepo.GetKey(results.Single(pair => pair.Key == "key").Value)).Permissions);

                authUrl = BTCPayServerClient.GenerateAuthorizeUri(tester.PayTester.ServerUri,
                    new[] { Policies.CanModifyStoreSettings, Policies.CanModifyServerSettings }, false, true).ToString();

                s.Driver.Navigate().GoToUrl(authUrl);
                Assert.DoesNotContain("kukksappname", s.Driver.PageSource);

                Assert.Equal("checkbox", s.Driver.FindElement(By.Id("btcpay.store.canmodifystoresettings")).GetAttribute("type").ToLowerInvariant());
                Assert.Equal("true", s.Driver.FindElement(By.Id("btcpay.store.canmodifystoresettings")).GetAttribute("value").ToLowerInvariant());
                Assert.Equal("checkbox", s.Driver.FindElement(By.Id("btcpay.server.canmodifyserversettings")).GetAttribute("type").ToLowerInvariant());
                Assert.Equal("true", s.Driver.FindElement(By.Id("btcpay.server.canmodifyserversettings")).GetAttribute("value").ToLowerInvariant());

                s.SetCheckbox(s, "btcpay.server.canmodifyserversettings", false);
                Assert.Contains("change-store-mode", s.Driver.PageSource);
                s.Driver.FindElement(By.Id("consent-yes")).Click();
                url = s.Driver.Url;
                results = url.Split("?").Last().Split("&")
                    .Select(s1 => new KeyValuePair<string, string>(s1.Split("=")[0], s1.Split("=")[1]));

                await TestApiAgainstAccessToken(results.Single(pair => pair.Key == "key").Value, tester, user,
                    (await apiKeyRepo.GetKey(results.Single(pair => pair.Key == "key").Value)).Permissions);

            }
        }

        async Task TestApiAgainstAccessToken(string accessToken, ServerTester tester, TestAccount testAccount,
            string expectedPermissionsString)
        {
            var expectedPermissions = Permission.ToPermissions(expectedPermissionsString).ToArray();
            expectedPermissions ??= new Permission[0];
            var apikeydata = await TestApiAgainstAccessToken<ApiKeyData>(accessToken, $"api/v1/api-keys/current", tester.PayTester.HttpClient);
            var permissions = apikeydata.Permissions;
            Assert.Equal(expectedPermissions.Length, permissions.Length);
            foreach (var expectPermission in expectedPermissions)
            {
                Assert.True(permissions.Any(p => p == expectPermission), $"Missing expected permission {expectPermission}");
            }

            if (permissions.Contains(Permission.Create(Policies.CanViewProfile)))
            {
                var resultUser = await TestApiAgainstAccessToken<string>(accessToken, $"{TestApiPath}/me/id", tester.PayTester.HttpClient);
                Assert.Equal(testAccount.UserId, resultUser);
            }
            else
            {
                await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
                {
                    await TestApiAgainstAccessToken<string>(accessToken, $"{TestApiPath}/me/id", tester.PayTester.HttpClient);
                });
            }
            //create a second user to see if any of its data gets messed upin our results.
            var secondUser = tester.NewAccount();
            secondUser.GrantAccess();

            var canModifyAllStores = Permission.Create(Policies.CanModifyStoreSettings, null);
            var canModifyServer = Permission.Create(Policies.CanModifyServerSettings, null);
            var unrestricted = Permission.Create(Policies.Unrestricted, null);
            var selectiveStorePermissions = permissions.Where(p => p.StoreId != null && p.Policy == Policies.CanModifyStoreSettings);
            if (permissions.Contains(canModifyAllStores) || selectiveStorePermissions.Any())
            {
                var resultStores =
                    await TestApiAgainstAccessToken<StoreData[]>(accessToken, $"{TestApiPath}/me/stores",
                        tester.PayTester.HttpClient);

                foreach (var selectiveStorePermission in selectiveStorePermissions)
                {
                    Assert.True(await TestApiAgainstAccessToken<bool>(accessToken,
                        $"{TestApiPath}/me/stores/{selectiveStorePermission.StoreId}/can-edit",
                        tester.PayTester.HttpClient));

                    Assert.Contains(resultStores,
                        data => data.Id.Equals(selectiveStorePermission.StoreId, StringComparison.InvariantCultureIgnoreCase));
                }

                bool shouldBeAuthorized = false;
                if (permissions.Contains(canModifyAllStores) || selectiveStorePermissions.Contains(Permission.Create(Policies.CanViewStoreSettings, testAccount.StoreId)))
                {
                    Assert.True(await TestApiAgainstAccessToken<bool>(accessToken,
                        $"{TestApiPath}/me/stores/{testAccount.StoreId}/can-view",
                        tester.PayTester.HttpClient));
                    Assert.Contains(resultStores,
                        data => data.Id.Equals(testAccount.StoreId, StringComparison.InvariantCultureIgnoreCase));
                    shouldBeAuthorized = true;
                }
                if (permissions.Contains(canModifyAllStores) || selectiveStorePermissions.Contains(Permission.Create(Policies.CanModifyStoreSettings, testAccount.StoreId)))
                {
                    Assert.True(await TestApiAgainstAccessToken<bool>(accessToken,
                        $"{TestApiPath}/me/stores/{testAccount.StoreId}/can-view",
                        tester.PayTester.HttpClient));
                    Assert.True(await TestApiAgainstAccessToken<bool>(accessToken,
                        $"{TestApiPath}/me/stores/{testAccount.StoreId}/can-edit",
                        tester.PayTester.HttpClient));
                    Assert.Contains(resultStores,
                        data => data.Id.Equals(testAccount.StoreId, StringComparison.InvariantCultureIgnoreCase));
                    shouldBeAuthorized = true;
                }

                if (!shouldBeAuthorized)
                {
                    await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
                    {
                        await TestApiAgainstAccessToken<bool>(accessToken,
                        $"{TestApiPath}/me/stores/{testAccount.StoreId}/can-edit",
                        tester.PayTester.HttpClient);
                    });
                    await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
                    {
                        await TestApiAgainstAccessToken<bool>(accessToken,
                        $"{TestApiPath}/me/stores/{testAccount.StoreId}/can-view",
                        tester.PayTester.HttpClient);
                    });
                    Assert.DoesNotContain(resultStores,
                        data => data.Id.Equals(testAccount.StoreId, StringComparison.InvariantCultureIgnoreCase));
                }
            }
            else if (!permissions.Contains(unrestricted))
            {

                await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
                {
                    await TestApiAgainstAccessToken<bool>(accessToken,
                        $"{TestApiPath}/me/stores/{testAccount.StoreId}/can-edit",
                        tester.PayTester.HttpClient);
                });
            }
            else
            {
                await TestApiAgainstAccessToken<bool>(accessToken,
                    $"{TestApiPath}/me/stores/{testAccount.StoreId}/can-edit",
                    tester.PayTester.HttpClient);
            }

            if (!permissions.Contains(unrestricted))
            {
                await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
                {
                    await TestApiAgainstAccessToken<bool>(accessToken, $"{TestApiPath}/me/stores/{secondUser.StoreId}/can-edit",
                        tester.PayTester.HttpClient);
                });
            }
            else
            {
                await TestApiAgainstAccessToken<bool>(accessToken, $"{TestApiPath}/me/stores/{secondUser.StoreId}/can-edit",
                    tester.PayTester.HttpClient);
            }

            if (permissions.Contains(canModifyServer))
            {
                Assert.True(await TestApiAgainstAccessToken<bool>(accessToken,
                    $"{TestApiPath}/me/is-admin",
                    tester.PayTester.HttpClient));
            }
            else
            {
                await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
                {
                    await TestApiAgainstAccessToken<bool>(accessToken,
                        $"{TestApiPath}/me/is-admin",
                        tester.PayTester.HttpClient);
                });
            }
        }

        public async Task<T> TestApiAgainstAccessToken<T>(string apikey, string url, HttpClient client)
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Get,
                new Uri(client.BaseAddress, url));
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("token", apikey);
            var result = await client.SendAsync(httpRequest);
            result.EnsureSuccessStatusCode();

            var rawJson = await result.Content.ReadAsStringAsync();
            if (typeof(T).IsPrimitive || typeof(T) == typeof(string))
            {
                return (T)Convert.ChangeType(rawJson, typeof(T));
            }

            return JsonConvert.DeserializeObject<T>(rawJson, BTCPayServer.Client.Serializer.GlobalSerializerSettings);
        }
    }
}

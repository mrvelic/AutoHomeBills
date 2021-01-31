using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Quartz;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoBills
{
    public class BillsJob : IJob
    {
        static string[] GoogleScopes = { SheetsService.Scope.Spreadsheets };
        static string GoogleAppName = "AutoBills";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<BillsJob> _log;
        private readonly IOptions<BillsWorkerOptions> _settings;

        public BillsJob(IHttpClientFactory httpClientFactory, ILogger<BillsJob> log, IOptions<BillsWorkerOptions> settings)
        {
            _log = log;
            _httpClientFactory = httpClientFactory;
            _settings = settings;
        }

        public Task Execute(IJobExecutionContext context)
        {
            return CheckForBills(context.CancellationToken);
        }

        public async Task CheckForBills(CancellationToken cancellationToken)
        {
            _log.LogInformation("Checking for bills...");

            var sheetService = await GetSheetsService();
            var client = _httpClientFactory.CreateClient(typeof(BillsJob).Name);

            client.BaseAddress = new Uri(_settings.Value.NetBankingAddress);
            client.DefaultRequestHeaders.Add("User-Agent", _settings.Value.UserAgent);

            var loginResult = await PerformLogin(client, _settings.Value.Username, _settings.Value.Password);

            if (!loginResult.Valid)
            {
                _log.LogError("Could not login to the internet banking service :( Error: {0}", loginResult.ErrorCode);
                return;
            }

            // load balances for the cookies
            var balancesResponse = await LoadBalances(client);

            var transactionData = await LoadTransactionDetails(
                client,
                referrer: balancesResponse.RequestMessage.RequestUri,
                accountNumber: _settings.Value.AccountNumber,
                beginDate: DateTime.Now.AddMonths(-1).AddDays(1),
                endDate: DateTime.Now
            );

            var matchingTransactions = transactionData
                .TransactionDetails
                .Where(x => _settings.Value.MerchantNames.Any(x.Description.Contains))
                .Select(x =>
                {
                    x.Description = x.Description
                        .Replace("VISA Purchase", string.Empty)
                        .Replace("0505", string.Empty)
                        .Trim();

                    return x;
                })
                .OrderBy(x => x.EffectiveDate);

            if (matchingTransactions.Any())
            {
                await ProcessTransactions(sheetService, matchingTransactions);
            }

            await PerformLogout(client, referrer: balancesResponse.RequestMessage.RequestUri);
        }

        private async Task ProcessTransactions(SheetsService sheetService, IEnumerable<Models.TransactionLineItem> matchingTransactions)
        {
            var sheetCounters = new Dictionary<string, int>();

            foreach (var sheetName in _settings.Value.PersonalSheetNames)
            {
                var valueResult = await sheetService.Spreadsheets.Values.Get(_settings.Value.GoogleSheetId, $"{sheetName}!A:C").ExecuteAsync();
                sheetCounters[sheetName] = valueResult.Values.Count() + 1;
            }

            var billsSheetName = _settings.Value.BillsSheetName;
            var existingSheetItems = await sheetService.Spreadsheets.Values.Get(_settings.Value.GoogleSheetId, $"{billsSheetName}!A:C").ExecuteAsync();
            sheetCounters[billsSheetName] = existingSheetItems.Values.Count() + 1;

            foreach (var t in matchingTransactions)
            {
                var sheetDate = t.EffectiveDate.ToString("dd/MM/yyyy");
                var sheetAmount = $"{t.DebitAmount:0.00}";

                var existsInSheet = existingSheetItems.Values.Any(row =>
                {
                    return row[0]?.ToString() == sheetDate
                        && row[1]?.ToString() == t.Description
                        && row[2]?.ToString() == $"${sheetAmount}";
                });

                if (!existsInSheet)
                {
                    _log.LogInformation("t: {0} {1,-70} {2,10:C}", t.EffectiveDate, t.Description, t.DebitAmount);

                    await AppendToSheet(
                        sheetService,
                        billsSheetName,
                        $"A{sheetCounters[billsSheetName]}",
                        new object[]
                        {
                            sheetDate,
                            t.Description,
                            sheetAmount,
                            3, // split divider
                            $"=C{sheetCounters[billsSheetName]}/D{sheetCounters[billsSheetName]}", // split amount formula
                            "Yes", // paid or not
                            $"{sheetDate} (Auto)" // date paid
                        }
                    );

                    foreach (var personSheetName in _settings.Value.PersonalSheetNames)
                    {
                        await AppendToSheet(
                            sheetService,
                            personSheetName,
                            $"A{sheetCounters[personSheetName]}",
                            new object[]
                            {
                                $"={billsSheetName}!A{sheetCounters[billsSheetName]}", // bills sheet date
                                $"={billsSheetName}!B{sheetCounters[billsSheetName]}", // bills sheet description
                                null, // CR
                                $"={billsSheetName}!E{sheetCounters[billsSheetName]}" // DR, bills sheet split amount
                            }
                        );

                        sheetCounters[personSheetName]++;
                    }

                    sheetCounters[billsSheetName]++;
                }
            }
        }

        private async Task<SheetsService> GetSheetsService()
        {
            using (var keyStream = new FileStream(_settings.Value.GoogleKeyFile, FileMode.Open, FileAccess.Read))
            {
                var credentials = (await GoogleCredential.FromStreamAsync(keyStream, CancellationToken.None))
                    .CreateScoped(GoogleScopes)
                    .CreateWithUser(_settings.Value.GoogleDelegatedAuthority);

                return new SheetsService(new BaseClientService.Initializer()
                {
                    HttpClientInitializer = credentials,
                    ApplicationName = GoogleAppName,
                    GZipEnabled = true
                });
            }
        }

        private async Task AppendToSheet(SheetsService service, string sheetName, string range, object[] rowData)
        {
            var data = new ValueRange { Values = new[] { rowData } };

            var request = service.Spreadsheets.Values.Update(data, _settings.Value.GoogleSheetId, $"{sheetName}!{range}");
            request.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

            await request.ExecuteAsync();
        }

        private Task<HttpResponseMessage> PerformLogout(HttpClient client, Uri referrer)
        {
            var logoutRequest = new HttpRequestMessage(HttpMethod.Get, "/logout");
            logoutRequest.Headers.Add("Referrer", referrer.ToString());
            logoutRequest.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9");

            return client.SendAsync(logoutRequest);
        }

        private async Task<Models.LoginResponse> PerformLogin(HttpClient client, string memberNumber, string password)
        {
            var loginPageResponse = await client.GetAsync("/");
            if (loginPageResponse.IsSuccessStatusCode)
            {
                var loginPageHtml = new HtmlDocument();
                loginPageHtml.LoadHtml(await loginPageResponse.Content.ReadAsStringAsync());

                var loginPostRequest = new HttpRequestMessage(HttpMethod.Post, "/api/ajaxlogin/login");

                loginPostRequest.Headers.Add("X-Requested-With", "XMLHttpRequest");
                loginPostRequest.Headers.Add("sec-fetch-dest", "empty");
                loginPostRequest.Headers.Add("sec-fetch-mode", "cors");
                loginPostRequest.Headers.Add("sec-fetch-site", "same-origin");
                loginPostRequest.Headers.Add("Origin", client.BaseAddress.ToString());
                loginPostRequest.Headers.Add("Referrer", client.BaseAddress.ToString());
                loginPostRequest.Headers.Add("Accept", "*/*");

                var loginPostValues = new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = loginPageHtml.DocumentNode.SelectSingleNode("//input[@name='__RequestVerificationToken']")?.GetAttributeValue("value", string.Empty),
                    ["DefaultUrl"] = loginPageHtml.GetElementbyId("DefaultUrl")?.GetAttributeValue("value", string.Empty),
                    ["Factor2Url"] = loginPageHtml.GetElementbyId("Factor2Url")?.GetAttributeValue("value", string.Empty),
                    ["OtpUrl"] = loginPageHtml.GetElementbyId("OtpUrl")?.GetAttributeValue("value", string.Empty),
                    ["DeniedUrl"] = loginPageHtml.GetElementbyId("DeniedUrl")?.GetAttributeValue("value", string.Empty),
                    ["PersonaLandingUrl"] = loginPageHtml.GetElementbyId("PersonaLandingUrl")?.GetAttributeValue("value", string.Empty),

                    ["MemberNumber"] = memberNumber,
                    ["Password"] = password
                };

                loginPostRequest.Content = new FormUrlEncodedContent(loginPostValues);

                var loginResult = await client.SendAsync(loginPostRequest);
                if (loginResult.IsSuccessStatusCode)
                {
                    return JsonConvert.DeserializeObject<Models.LoginResponse>(await loginResult.Content.ReadAsStringAsync());
                }
            }

            return new Models.LoginResponse();
        }

        private Task<HttpResponseMessage> LoadBalances(HttpClient client)
        {
            var balancesGetRequest = new HttpRequestMessage(HttpMethod.Get, "/accounts/balances/");
            balancesGetRequest.Headers.Add("Referrer", client.BaseAddress.ToString());
            balancesGetRequest.Headers.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9");

            return client.SendAsync(balancesGetRequest);
        }

        private async Task<List<Models.AccountDataResponse>> LoadAccountData(HttpClient client, Uri referrer)
        {
            var accountDataPostRequest = new HttpRequestMessage(HttpMethod.Post, "/platform.axd?u=account%2FGetAccountsBasicData");
            accountDataPostRequest.Headers.Add("Origin", client.BaseAddress.ToString());
            accountDataPostRequest.Headers.Add("Referrer", referrer.ToString());
            accountDataPostRequest.Headers.Add("Accept", "application/json; charset=utf-8");

            accountDataPostRequest.Content = new StringContent(JsonConvert.SerializeObject(new { ForceFetchData = false }), Encoding.UTF8, "application/json");

            var accountDataResponse = await client.SendAsync(accountDataPostRequest);

            if (accountDataResponse.IsSuccessStatusCode)
            {
                return JsonConvert.DeserializeObject<List<Models.AccountDataResponse>>(await accountDataResponse.Content.ReadAsStringAsync());
            }

            return null;
        }

        private async Task<Models.TransactionDetailsResponse> LoadTransactionDetails(HttpClient client, Uri referrer, string accountNumber, DateTime beginDate, DateTime endDate)
        {
            var transactionDataPostRequest = new HttpRequestMessage(HttpMethod.Post, "/platform.axd?u=transaction%2FGetTransactionHistory");
            transactionDataPostRequest.Headers.Add("Origin", client.BaseAddress.ToString());
            transactionDataPostRequest.Headers.Add("Referrer", referrer.ToString());
            transactionDataPostRequest.Headers.Add("Accept", "application/json; charset=utf-8");

            var transactionRequestData = new
            {
                AccountNumber = accountNumber,
                BeginDate = beginDate.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                EndDate = endDate.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                NewestTransactionFirst = true,
                TransactionTypeId = 1900,
                isSearchFiltered = false
            };

            transactionDataPostRequest.Content = new StringContent(JsonConvert.SerializeObject(transactionRequestData), Encoding.UTF8, "application/json");

            var transactionDataResponse = await client.SendAsync(transactionDataPostRequest);
            if (transactionDataResponse.IsSuccessStatusCode)
            {
                return JsonConvert.DeserializeObject<Models.TransactionDetailsResponse>(await transactionDataResponse.Content.ReadAsStringAsync());
            }

            return null;
        }
    }
}

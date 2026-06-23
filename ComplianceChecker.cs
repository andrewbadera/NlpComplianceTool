using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using OpenAI.Chat;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ComplianceCheck
{
    internal class ComplianceChecker
    {
        private readonly IConfiguration _configuration;
        private ILogger<ComplianceChecker> _logger;

        public ComplianceChecker(IConfiguration configuration, ILogger<ComplianceChecker> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }
        public async Task CheckCompliance()
        {
            string aisearchUrl = _configuration["AzureSearch:Url"];
            string aisearchKey = _configuration["AzureSearch:ApiKey"];
            string searchIndex = _configuration["AzureSearch:CriteriaIndex"];
            string webPageIndex = _configuration["AzureSearch:WebpageIndex"];

            _logger.LogDebug($"Azure Search URL: {aisearchUrl}");
            _logger.LogDebug($"AI Search Index: {searchIndex}");

            _logger.LogDebug($"AI Search API Key Default: " +
                $"{aisearchKey == "{aiSearchKey}"}");

            _logger.LogDebug($"AI Search Criteria Index: {searchIndex}");

            _logger.LogDebug($"AI Search Webpage Index: {webPageIndex}");

            var endpoint = _configuration["AzureOpenAI:Endpoint"];
            _logger.LogDebug($"Foundry Endpoint: {endpoint}");

            var deploymentName = _configuration["AzureOpenAI:DeploymentName"];
            _logger.LogDebug($"Foundry Deployment Name: {deploymentName}");

            var apiKey = _configuration["AzureOpenAI:ApiKey"];
            _logger.LogDebug($"Foundry API Key Default: {apiKey == "{openApiKey}"}");

            var endpointUri = new Uri(endpoint);

            AzureOpenAIClient azureClient = new(
                endpointUri,
                new AzureKeyCredential(apiKey));
            ChatClient chatClient = azureClient.GetChatClient(deploymentName);

            string searchEndpoint = aisearchUrl;

            var webpageSearchClient = new SearchClient(
                new Uri(aisearchUrl),
                webPageIndex,
                new Azure.AzureKeyCredential(aisearchKey));

            if (webpageSearchClient == null)
            {
                _logger.LogError("Failed to create webpageSearchClient.");
                return;
            }

            var allWebpages = new ConcurrentBag<SearchDocumentModel>();

            var webpageResults = webpageSearchClient.Search<SearchDocumentModel>("*", new SearchOptions
            {
                Size = 1000
            });

            var uniqueTitles = webpageResults.Value.GetResults().DistinctBy(w => w.Document.document_title).ToList();

            var webpageCount = uniqueTitles.Count;

            var allResults = webpageResults.Value.GetResults();

            var resultsByTitle = allResults.GroupBy(r => r.Document.document_title);

            foreach (var result in allResults)
            {
                var doc = result.Document;
                if (doc != null)
                {
                    allWebpages.Add(doc);
                    _logger.LogInformation($"Retrieved partial webpage: {doc.document_title}");
                }
                else
                {
                    _logger.LogWarning("Document is null.");
                }
            }

            webpageCount -= 1000;

            ChatCompletionOptions options;

            try
            {
#pragma warning disable AOAI001 // Suppress the diagnostic warning
                options = new ChatCompletionOptions()
                {
                    TopP = float.Parse(_configuration["ChatCompletion:TopP"]),
                    Temperature = float.Parse(_configuration["ChatCompletion:Temperature"]),
                    MaxOutputTokenCount = int.Parse(_configuration["ChatCompletion:MaxOutputTokenCount"]),
                };

                options.AddDataSource(new AzureSearchChatDataSource()
                {
                    Endpoint = new Uri(searchEndpoint),
                    IndexName = searchIndex,
                    Authentication = DataSourceAuthentication.FromApiKey(aisearchKey),
                    MaxSearchQueries = int.Parse(_configuration["ChatCompletion:MaxSearchQueries"]),
                    TopNDocuments = int.Parse(_configuration["ChatCompletion:TopNDocuments"]),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set up ChatCompletionOptions.");
                return;
            }

            var complianceResults = new ConcurrentBag<ComplianceResult>();
            var errorDict = new ConcurrentDictionary<string, string>();

            // read contents of SystemMessage.txt
            var systemMessage = System.IO.File.ReadAllText("SystemMessage.txt");

            int maxRetries = int.Parse(_configuration["Retry:MaxRetries"]);
            int delayMs = int.Parse(_configuration["Retry:DelayMs"]);

            var validWebpages = allWebpages.Where(doc => doc != null).ToList();

            string lastMessage = "";
            string currentMessage = "";
            string userMessage = "";
            var uniqueDocs = validWebpages.Select(doc => doc).Distinct().ToList();
            foreach (var title in uniqueDocs)
            {
                var allContent = validWebpages.Where(doc => doc.document_title == title.document_title).ToList();
                foreach (var content in allContent)
                {
                    currentMessage += content.content_text;
                }

                List<ChatMessage> messages = new List<ChatMessage>();
                messages.Add(new SystemChatMessage(systemMessage));
                messages.Add(new UserChatMessage(currentMessage));
                ChatCompletion completion = completion = await chatClient.CompleteChatAsync(
                       messages,
                       options
                   );

                var resultJson = completion.Content[0].Text.Trim();
                if (resultJson == null)
                {
                    errorDict.AddOrUpdate(title.document_title, $"\"NullResult|{userMessage}\"", (k, k0) => k);
                    _logger.LogError($"Null result for: {title.document_title}");
                    continue;
                }

                _logger.LogInformation("AI Response:\n" + resultJson);

                try
                {
                    var result = System.Text.Json.JsonSerializer.Deserialize<ComplianceResult>(resultJson);
                    result.Filename = title.document_title;
                    result.InputTokens = completion.Usage.InputTokenCount;
                    result.OutputTokens = completion.Usage.OutputTokenCount;
                    result.Title = $"\"{result.Title}\"";
                    result.Reason = $"\"{result.Reason}\"";
                    complianceResults.Add(result);
                    _logger.LogInformation($"processed: {title.document_title}");
                }
                catch (Exception ex)
                {
                    errorDict.AddOrUpdate($"{title.document_title}", $"\"Therequestedinfo|{userMessage}\"", (k, k0) => k);
                    _logger.LogError(ex, $"errored: ${title.document_title}");
                }
            }

            File.Delete("complianceresults.csv");
            File.Delete("errordocs.csv");

            using (var writer = new StreamWriter("complianceresults.csv"))
            {
                writer.WriteLine("Filename,Title,Compliant,Reason,InputTokens,OutputTokens");
                foreach (var record in complianceResults)
                {
                    writer.WriteLine($"{record.Filename},{record.Title},{record.Compliant},{record.Reason},{record.InputTokens},{record.OutputTokens}");
                }
            }

            _logger.LogInformation($"Number of documents producing errors: {errorDict.Count}");
            using (var writer = new StreamWriter("errordocs.csv"))
            {
                writer.WriteLine("Filename,Content");
                foreach (var record in errorDict)
                {
                    writer.WriteLine($"{record.Key},{record.Value}");
                }
            }


            return;
        }
    }
}

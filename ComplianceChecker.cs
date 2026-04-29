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
            string searchIndex = _configuration["AzureSearch:MultimodalIndex"];
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

            var webpageCount = webpageSearchClient.GetDocumentCount().Value;

            var allWebpages = new ConcurrentBag<SearchDocumentModel>();

            while (webpageCount > 0)
            {
                var webpageResults = webpageSearchClient.Search<SearchDocumentModel>("*", new SearchOptions
                {
                    Size = 1000
                });

                foreach (var result in webpageResults.Value.GetResults())
                {
                    var doc = result.Document;
                    if (doc != null)
                    {
                        allWebpages.Add(doc);
                        Console.WriteLine($"Retrieved webpage: {doc.title}");
                    }
                    else
                    {
                        Console.WriteLine("Document is null.");
                    }
                }

                webpageCount -= 1000;
            }

#pragma warning disable AOAI001 // Suppress the diagnostic warning
            ChatCompletionOptions options = new ChatCompletionOptions()
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

            var complianceResults = new ConcurrentBag<ComplianceResult>();
            var errorDict = new ConcurrentDictionary<string, string>();
            
            foreach (var webpageDoc in allWebpages)
            {
                // read contents of SystemMessage.txt
                var systemMessage = System.IO.File.ReadAllText("SystemMessage.txt");
                var userMessage = webpageDoc.content.Trim();
                List<ChatMessage> messages = new List<ChatMessage>();
                messages.Add(new SystemChatMessage(systemMessage));

                messages.Add(new UserChatMessage(userMessage));

                try
                {
                    ChatCompletion completion = null;

                    try { 
                        int retryCount = 0;
                        int maxRetries = int.Parse(_configuration["Retry:MaxRetries"]);
                        int delayMs = int.Parse(_configuration["Retry:DelayMs"]);

                        while (retryCount <= maxRetries)
                        {
                            try
                            {
                                completion = await chatClient.CompleteChatAsync(
                                    messages,
                                    options
                                );
                                break;
                            }
                            catch (Exception retryEx)
                            {
                                if (retryCount < maxRetries)
                                {
                                    Console.WriteLine($"Retry after 1 second for {webpageDoc.title}");
                                    await Task.Delay(delayMs);
                                    retryCount++;
                                }
                                else
                                {
                                    throw;
                                }
                            }
                        }
                    } catch (Exception ex)
                    {
                        errorDict.AddOrUpdate(webpageDoc.title, $"\"ChatCompletionEx|{userMessage}\"", (k, k0) => k);
                        Console.WriteLine($"errored: {webpageDoc.title} - \"{ex.Message}\"");
                        continue;
                    }

                    var resultJson = completion.Content[0].Text.Trim();

                    Console.WriteLine("AI Response:\n" + resultJson);

                    try
                    {
                        var result = System.Text.Json.JsonSerializer.Deserialize<ComplianceResult>(resultJson);
                        result.Filename = webpageDoc.title;
                        result.InputTokens = completion.Usage.InputTokenCount;
                        result.OutputTokens = completion.Usage.OutputTokenCount;
                        result.Title = $"\"{result.Title}\"";
                        result.Reason = $"\"{result.Reason}\"";
                        complianceResults.Add(result);
                        Console.WriteLine($"processed: {webpageDoc.title}");
                    }
                    catch (Exception ex)
                    {
                        errorDict.AddOrUpdate($"{webpageDoc.title}", $"\"Therequestedinfo|{userMessage}\"", (k, k0) => k);
                        Console.WriteLine($"errored: ${webpageDoc.title}");
                    }
                }
                catch (Exception ex)
                {
                    errorDict.AddOrUpdate(webpageDoc.title, $"\"OtherEx|{userMessage}\"", (k, k0) => k);
                    Console.WriteLine($"errored: {webpageDoc.title} - \"{ex.Message}\"");
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

            Console.WriteLine($"Number of documents producing errors: {errorDict.Count}");
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

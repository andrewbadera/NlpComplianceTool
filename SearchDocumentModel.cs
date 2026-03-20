namespace ComplianceCheck
{
    using Azure.Search.Documents.Indexes;
    using Azure.Search.Documents.Indexes.Models;
    using System.Collections.Generic;

    public class SearchDocumentModel
    {
        public string title { get; set; }
      
        public string content { get; set; }
    }
}
output "search_endpoint" {
  value       = "https://${azurerm_search_service.main.name}.search.windows.net"
  description = "SEARCH_ENDPOINT for InvoiceIndexer"
}

output "openai_endpoint" {
  value       = "https://${azurerm_cognitive_account.openai.custom_subdomain_name}.openai.azure.com/"
  description = "OPENAI_ENDPOINT for InvoiceIndexer"
}

output "storage_account_url" {
  value       = azurerm_storage_account.documents.primary_blob_endpoint
  description = "STORAGE_ACCOUNT_URL for InvoiceIndexer"
}

output "storage_container" {
  value       = azurerm_storage_container.documents.name
  description = "STORAGE_CONTAINER for InvoiceIndexer"
}

output "resource_group_name" {
  value       = azurerm_resource_group.main.name
  description = "Name of the main resource group"
}

output "document_intelligence_endpoint" {
  value       = azurerm_cognitive_account.document_intelligence.endpoint
  description = "DOCUMENT_INTELLIGENCE_ENDPOINT for extraction comparison"
}
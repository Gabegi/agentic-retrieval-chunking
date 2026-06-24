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

output "storage_container_csv" {
  value       = azurerm_storage_container.documents_csv.name
  description = "STORAGE_CONTAINER for eval result writes"
}

output "openai_embedding_deployment" {
  value       = var.openai_embedding_deployment
  description = "OPENAI_EMBEDDING_DEPLOYMENT"
}

output "openai_gpt_deployment" {
  value       = var.openai_gpt_deployment
  description = "OPENAI_GPT_DEPLOYMENT"
}

output "openai_gpt_model_name" {
  value       = var.openai_gpt_model_name
  description = "OPENAI_GPT_MODEL_NAME"
}

output "openai_eval_deployment" {
  value       = var.openai_eval_deployment
  description = "OPENAI_EVAL_DEPLOYMENT"
}

output "search_index_name" {
  value       = var.search_index_name
  description = "SEARCH_INDEX_NAME"
}

output "knowledge_source_name" {
  value       = var.knowledge_source_name
  description = "KNOWLEDGE_SOURCE_NAME"
}

output "knowledge_base_name" {
  value       = var.knowledge_base_name
  description = "KNOWLEDGE_BASE_NAME"
}

output "resource_group_name" {
  value       = azurerm_resource_group.main.name
  description = "Name of the main resource group"
}

output "document_intelligence_endpoint" {
  value       = azurerm_cognitive_account.document_intelligence.endpoint
  description = "DOCUMENT_INTELLIGENCE_ENDPOINT for extraction comparison"
}
# storage.tf
# Storage account and blob container for RAG document ingestion

data "azurerm_client_config" "current" {}

resource "azurerm_storage_account" "documents" {
  name                     = "staccountchunkingrag"
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}

resource "azurerm_storage_container" "documents" {
  name                  = "documents"
  storage_account_id    = azurerm_storage_account.documents.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "protocols" {
  name                  = "protocols"
  storage_account_id    = azurerm_storage_account.documents.id
  container_access_type = "private"
}

resource "azurerm_role_assignment" "sp_blob_contributor" {
  scope                = azurerm_storage_account.documents.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}

resource "azurerm_role_assignment" "developer_blob_reader" {
  count                = var.developer_object_id != "" ? 1 : 0
  scope                = azurerm_storage_account.documents.id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = var.developer_object_id
}

resource "azurerm_role_assignment" "developer_document_intelligence" {
  count                = var.developer_object_id != "" ? 1 : 0
  scope                = azurerm_cognitive_account.document_intelligence.id
  role_definition_name = "Cognitive Services User"
  principal_id         = var.developer_object_id
}

output "sp_object_id" {
  value       = data.azurerm_client_config.current.object_id
  description = "Object ID used for the Storage Blob Data Contributor role assignment"
}
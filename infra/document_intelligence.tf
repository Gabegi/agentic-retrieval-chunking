resource "azurerm_cognitive_account" "document_intelligence" {
  name                  = "di-rag-chunking-dev"
  resource_group_name   = azurerm_resource_group.main.name
  location              = azurerm_resource_group.main.location
  kind                  = "FormRecognizer"
  sku_name              = "S0"
  custom_subdomain_name = "di-rag-chunking-dev"

  tags = {
    project     = "agentic-rag-chunking"
    environment = "dev"
  }
}

# Allow runner/developer to call Document Intelligence locally
resource "azurerm_role_assignment" "runner_document_intelligence" {
  scope                = azurerm_cognitive_account.document_intelligence.id
  role_definition_name = "Cognitive Services User"
  principal_id         = data.azurerm_client_config.current.object_id
}

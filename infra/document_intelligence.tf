# ---------------------------------------------------------------------------
# Document Intelligence ("prebuilt-layout") - called through the existing
# Foundry multi-service AI Services account (data.azurerm_cognitive_account.
# foundry, see data.tf), not a standalone Cognitive Services account. Same
# resource/endpoint/private connectivity already used for the OpenAI
# deployments (ai_deployments.tf, app_service.tf, function_app.tf) - no new
# azurerm_cognitive_account or azurerm_private_endpoint needed here.
# ---------------------------------------------------------------------------

# Scoped to the Foundry project (not the whole account), same reasoning as
# the openai_user role assignments in app_service.tf/function_app.tf: the
# project only exists to narrow RBAC down from account-wide.
resource "azurerm_role_assignment" "func_document_intelligence_user" {
  scope = data.azurerm_cognitive_account_project.rag.id
  # No Document-Intelligence-specific built-in role exists (checked via
  # `az role definition list` - only Form Recognizer's older siblings like
  # OpenAI/Language/Speech get dedicated roles). "Cognitive Services User" is
  # the generic data-plane role Microsoft's docs specify for AAD-based
  # Document Intelligence calls (Analyze).
  role_definition_name = "Cognitive Services User"
  principal_id         = azurerm_windows_function_app.indexer.identity[0].principal_id
}

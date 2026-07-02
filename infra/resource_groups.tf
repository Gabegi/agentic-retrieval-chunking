# ---------------------------------------------------------------------------
# Resource groups owned/created by this Terraform config (as opposed to the
# network/ai/data RGs in data.tf, which are owned by the platform team).
# ---------------------------------------------------------------------------

resource "azurerm_resource_group" "api" {
  name     = "cor-cap-api-${local.env}-${local.region}-${local.instance}"
  location = var.location
  tags     = local.common_tags
}

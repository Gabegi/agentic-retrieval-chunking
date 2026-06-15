variable "subscription_id" {
  description = "Azure subscription ID to deploy resources into"
  type        = string
}

variable "openai_embedding_deployment" {
  description = "Name of the Azure OpenAI embedding deployment"
  type        = string
  default     = "text-embedding-3-large"
}

variable "openai_gpt_deployment" {
  description = "Name of the Azure OpenAI GPT deployment"
  type        = string
  default     = "querying"
}

variable "openai_gpt_model_name" {
  description = "Model name for the Azure OpenAI GPT deployment"
  type        = string
  default     = "gpt-4.1-mini"
}

variable "openai_extraction_deployment" {
  description = "Name of the Azure OpenAI gpt-4.1 deployment used for invoice field extraction"
  type        = string
  default     = "gpt-41-extraction"
}

variable "search_index_name" {
  description = "Name of the Azure AI Search index"
  type        = string
  default     = "invoices"
}

variable "knowledge_source_name" {
  description = "Name of the AI Search knowledge source"
  type        = string
  default     = "invoices-knowledge-source"
}

variable "knowledge_base_name" {
  description = "Name of the AI Search knowledge base"
  type        = string
  default     = "invoices-knowledge-base"
}

variable "indexer_image_tag" {
  description = "Docker image tag for the invoice indexer container"
  type        = string
  default     = "latest"
}

variable "admin_object_id" {
  description = "Object ID of the admin user account for portal access to the search service"
  type        = string
}
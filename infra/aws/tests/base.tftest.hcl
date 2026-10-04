mock_provider "aws" {
  mock_data "aws_availability_zones" {
    defaults = { names = ["us-east-1a", "us-east-1b"] }
  }
  mock_data "aws_caller_identity" {
    defaults = { account_id = "123456789012" }
  }
  mock_data "aws_iam_policy_document" {
    defaults = { json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}" }
  }
}

run "base_without_services" {
  command = plan
  variables {
    aws_region = "us-east-1"
  }
  assert {
    condition     = length(aws_ecs_service.api) == 0 && length(aws_ecs_service.worker) == 0 && length(aws_ecs_service.portal) == 0
    error_message = "A primeira fase não deve iniciar serviços ECS."
  }
  assert {
    condition     = output.export_bucket == "gerador-excel-prod-123456789012-us-east-1"
    error_message = "O nome do bucket precisa ser previsível e único por conta/região."
  }
  assert {
    condition     = aws_acm_certificate.web.domain_name == "apiga.vianadev.com.br"
    error_message = "A primeira fase deve solicitar o certificado antes da ativação do ECS."
  }
}

run "runtime_with_services" {
  command = plan
  variables {
    aws_region           = "us-east-1"
    deploy_app           = true
    api_domain           = "apiga.example.com"
    portal_domain        = "ga.example.com"
    portal_allowed_cidrs = ["203.0.113.10/32"]
    image_tag            = "test-1"
  }
  assert {
    condition     = length(aws_ecs_service.api) == 1 && length(aws_ecs_service.worker) == 1 && length(aws_ecs_service.portal) == 1
    error_message = "A segunda fase deve criar API, worker e portal."
  }
  assert {
    condition = (
      aws_ecs_service.api[0].network_configuration[0].assign_public_ip &&
      aws_ecs_service.worker[0].network_configuration[0].assign_public_ip &&
      aws_ecs_service.portal[0].network_configuration[0].assign_public_ip
    )
    error_message = "As três tasks precisam de IP público para saída à internet sem NAT."
  }
  assert {
    condition     = output.api_url == "https://apiga.example.com" && output.portal_url == "https://ga.example.com"
    error_message = "Os domínios públicos devem usar HTTPS."
  }
}

resource "aws_ecs_cluster" "app" {
  name = local.name
  setting {
    name  = "containerInsights"
    value = "enabled"
  }
}

resource "aws_acm_certificate" "web" {
  domain_name               = local.api_domain
  subject_alternative_names = [local.portal_domain]
  validation_method         = "DNS"

  lifecycle { create_before_destroy = true }
}

resource "aws_acm_certificate_validation" "web" {
  count           = var.deploy_app ? 1 : 0
  certificate_arn = aws_acm_certificate.web.arn
}

resource "aws_lb" "web" {
  count              = var.deploy_app ? 1 : 0
  name               = "${local.name}-alb"
  internal           = false
  load_balancer_type = "application"
  security_groups    = [aws_security_group.alb.id]
  subnets            = aws_subnet.public[*].id
}

resource "aws_lb_target_group" "api" {
  count       = var.deploy_app ? 1 : 0
  name        = "${local.name}-api"
  port        = 8080
  protocol    = "HTTP"
  target_type = "ip"
  vpc_id      = aws_vpc.app.id

  health_check {
    path                = "/health/ready"
    matcher             = "200"
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_lb_target_group" "portal" {
  count       = var.deploy_app ? 1 : 0
  name        = "${local.name}-portal"
  port        = 8080
  protocol    = "HTTP"
  target_type = "ip"
  vpc_id      = aws_vpc.app.id

  health_check {
    path                = "/health"
    matcher             = "200"
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_lb_listener" "http" {
  count             = var.deploy_app ? 1 : 0
  load_balancer_arn = aws_lb.web[0].arn
  port              = 80
  protocol          = "HTTP"
  default_action {
    type = "redirect"
    redirect {
      port        = "443"
      protocol    = "HTTPS"
      status_code = "HTTP_301"
    }
  }
}

resource "aws_lb_listener" "https" {
  count             = var.deploy_app ? 1 : 0
  load_balancer_arn = aws_lb.web[0].arn
  port              = 443
  protocol          = "HTTPS"
  certificate_arn   = aws_acm_certificate_validation.web[0].certificate_arn
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  default_action {
    type = "fixed-response"
    fixed_response {
      content_type = "text/plain"
      message_body = "Not found"
      status_code  = "404"
    }
  }
}

resource "aws_lb_listener_rule" "api" {
  count        = var.deploy_app ? 1 : 0
  listener_arn = aws_lb_listener.https[0].arn
  priority     = 10
  condition {
    host_header { values = [local.api_domain] }
  }
  action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api[0].arn
  }
}

resource "aws_lb_listener_rule" "portal" {
  count        = var.deploy_app ? 1 : 0
  listener_arn = aws_lb_listener.https[0].arn
  priority     = 20
  condition {
    host_header { values = [local.portal_domain] }
  }
  condition {
    source_ip { values = var.portal_allowed_cidrs }
  }
  action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.portal[0].arn
  }
}

locals {
  api_environment = [
    { name = "Storage__Provider", value = "MongoDB" },
    { name = "Mongo__Database", value = var.mongo_database },
    { name = "Queue__Provider", value = "SQS" },
    { name = "Aws__Region", value = var.aws_region },
    { name = "Aws__Bucket", value = aws_s3_bucket.exports.bucket },
    { name = "Aws__QueueUrl", value = aws_sqs_queue.jobs.url },
    { name = "PublicBaseUrl", value = "https://${local.api_domain}" },
    { name = "Swagger__Enabled", value = "true" },
    { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
  ]
  api_secrets = concat([
    { name = "Mongo__ConnectionString", valueFrom = aws_secretsmanager_secret.mongo_connection.arn },
    { name = "Security__ApiKey", valueFrom = aws_secretsmanager_secret.api_key.arn },
    { name = "Security__DownloadSigningKey", valueFrom = aws_secretsmanager_secret.download_signing_key.arn },
    ], var.resend_secret_arn == null ? [] : [
    { name = "Resend__ApiKey", valueFrom = var.resend_secret_arn },
  ])
  worker_environment = [
    { name = "Storage__Provider", value = "MongoDB" },
    { name = "Mongo__Database", value = var.mongo_database },
    { name = "Queue__Provider", value = "SQS" },
    { name = "Aws__Region", value = var.aws_region },
    { name = "Aws__Bucket", value = aws_s3_bucket.exports.bucket },
    { name = "Aws__QueueUrl", value = aws_sqs_queue.jobs.url },
  ]
  portal_environment = [
    { name = "Api__BaseUrl", value = "https://${local.api_domain}" },
    { name = "Proxy__KnownIpNetwork", value = var.vpc_cidr },
    { name = "ASPNETCORE_ENVIRONMENT", value = "Production" },
  ]
}

resource "aws_ecs_task_definition" "api" {
  count                    = var.deploy_app ? 1 : 0
  family                   = "${local.name}-api"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "512"
  memory                   = "1024"
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.api.arn
  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = var.cpu_architecture
  }
  container_definitions = jsonencode([{
    name      = "api"
    image     = "${aws_ecr_repository.api.repository_url}:${var.image_tag}"
    essential = true
    portMappings = [{
      containerPort = 8080
      protocol      = "tcp"
    }]
    environment = concat(local.api_environment, var.resend_from == null ? [] : [
      { name = "Resend__From", value = var.resend_from },
    ])
    secrets = local.api_secrets
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.api.name
        awslogs-region        = var.aws_region
        awslogs-stream-prefix = "api"
      }
    }
  }])
}

resource "aws_ecs_task_definition" "worker" {
  count                    = var.deploy_app ? 1 : 0
  family                   = "${local.name}-worker"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "1024"
  memory                   = "2048"
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.worker.arn
  ephemeral_storage { size_in_gib = 30 }
  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = var.cpu_architecture
  }
  container_definitions = jsonencode([{
    name        = "worker"
    image       = "${aws_ecr_repository.worker.repository_url}:${var.image_tag}"
    essential   = true
    environment = local.worker_environment
    secrets = [
      { name = "Mongo__ConnectionString", valueFrom = aws_secretsmanager_secret.mongo_connection.arn },
    ]
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.worker.name
        awslogs-region        = var.aws_region
        awslogs-stream-prefix = "worker"
      }
    }
  }])
}

resource "aws_ecs_task_definition" "portal" {
  count                    = var.deploy_app ? 1 : 0
  family                   = "${local.name}-portal"
  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"
  cpu                      = "256"
  memory                   = "512"
  execution_role_arn       = aws_iam_role.execution.arn
  task_role_arn            = aws_iam_role.portal.arn
  runtime_platform {
    operating_system_family = "LINUX"
    cpu_architecture        = var.cpu_architecture
  }
  container_definitions = jsonencode([{
    name      = "portal"
    image     = "${aws_ecr_repository.portal.repository_url}:${var.image_tag}"
    essential = true
    portMappings = [{
      containerPort = 8080
      protocol      = "tcp"
    }]
    environment = local.portal_environment
    secrets = [
      { name = "Portal__Password", valueFrom = aws_secretsmanager_secret.portal_password.arn },
      { name = "Api__Key", valueFrom = aws_secretsmanager_secret.api_key.arn },
    ]
    logConfiguration = {
      logDriver = "awslogs"
      options = {
        awslogs-group         = aws_cloudwatch_log_group.portal.name
        awslogs-region        = var.aws_region
        awslogs-stream-prefix = "portal"
      }
    }
  }])
}

resource "aws_ecs_service" "api" {
  count                             = var.deploy_app ? 1 : 0
  name                              = "${local.name}-api"
  cluster                           = aws_ecs_cluster.app.id
  task_definition                   = aws_ecs_task_definition.api[0].arn
  desired_count                     = 1
  launch_type                       = "FARGATE"
  health_check_grace_period_seconds = 90
  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.web.id]
    assign_public_ip = true
  }
  load_balancer {
    target_group_arn = aws_lb_target_group.api[0].arn
    container_name   = "api"
    container_port   = 8080
  }
  depends_on = [
    aws_lb_listener_rule.api,
    aws_iam_role_policy_attachment.execution,
    aws_iam_role_policy.execution_secrets,
    aws_iam_role_policy.api,
  ]
}

resource "aws_ecs_service" "worker" {
  count           = var.deploy_app ? 1 : 0
  name            = "${local.name}-worker"
  cluster         = aws_ecs_cluster.app.id
  task_definition = aws_ecs_task_definition.worker[0].arn
  desired_count   = 1
  launch_type     = "FARGATE"
  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.worker.id]
    assign_public_ip = true
  }
  depends_on = [
    aws_iam_role_policy_attachment.execution,
    aws_iam_role_policy.execution_secrets,
    aws_iam_role_policy.worker,
  ]
}

resource "aws_ecs_service" "portal" {
  count                             = var.deploy_app ? 1 : 0
  name                              = "${local.name}-portal"
  cluster                           = aws_ecs_cluster.app.id
  task_definition                   = aws_ecs_task_definition.portal[0].arn
  desired_count                     = 1
  launch_type                       = "FARGATE"
  health_check_grace_period_seconds = 90
  network_configuration {
    subnets          = aws_subnet.public[*].id
    security_groups  = [aws_security_group.web.id]
    assign_public_ip = true
  }
  load_balancer {
    target_group_arn = aws_lb_target_group.portal[0].arn
    container_name   = "portal"
    container_port   = 8080
  }
  depends_on = [
    aws_lb_listener_rule.portal,
    aws_iam_role_policy_attachment.execution,
    aws_iam_role_policy.execution_secrets,
  ]
}

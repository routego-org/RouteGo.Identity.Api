#!/bin/bash
# Runs when LocalStack is ready. Creates only what Identity publishes to, plus notification-q so a
# developer can see UserRegistered arrive:
#   awslocal sqs receive-message --queue-url http://localhost:4566/000000000000/notification-q
set -e
export AWS_DEFAULT_REGION=ap-south-1

awslocal sns create-topic --name routego-identity-events >/dev/null
awslocal sns create-topic --name routego-audit-events >/dev/null

awslocal sqs create-queue --queue-name notification-dlq >/dev/null
DLQ_ARN=$(awslocal sqs get-queue-attributes \
  --queue-url http://localhost:4566/000000000000/notification-dlq \
  --attribute-names QueueArn --query Attributes.QueueArn --output text)
awslocal sqs create-queue --queue-name notification-q --attributes \
  "{\"RedrivePolicy\":\"{\\\"deadLetterTargetArn\\\":\\\"$DLQ_ARN\\\",\\\"maxReceiveCount\\\":\\\"5\\\"}\",\"ReceiveMessageWaitTimeSeconds\":\"20\"}" >/dev/null

awslocal sns subscribe \
  --topic-arn arn:aws:sns:ap-south-1:000000000000:routego-identity-events \
  --protocol sqs \
  --notification-endpoint arn:aws:sqs:ap-south-1:000000000000:notification-q \
  --attributes '{"RawMessageDelivery":"true","FilterPolicy":"{\"eventType\":[\"UserRegistered\",\"OperatorApproved\",\"OperatorRejected\"]}"}' >/dev/null

echo "RouteGo Identity local resources ready"

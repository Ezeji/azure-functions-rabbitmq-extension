﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Azure.WebJobs.Description;
using Microsoft.Azure.WebJobs.Host.Bindings;
using Microsoft.Azure.WebJobs.Host.Config;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Microsoft.Azure.WebJobs.Extensions.RabbitMQ
{
    [Extension("RabbitMQ")]
    internal class RabbitMQExtensionConfigProvider : IExtensionConfigProvider
    {
        private readonly IOptions<RabbitMQOptions> _options;
        private readonly INameResolver _nameResolver;
        private readonly IRabbitMQServiceFactory _rabbitMQServiceFactory;
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;
        private readonly ConcurrentDictionary<string, IRabbitMQService> _connectionParametersToService;

        public RabbitMQExtensionConfigProvider(IOptions<RabbitMQOptions> options, INameResolver nameResolver, IRabbitMQServiceFactory rabbitMQServiceFactory, ILoggerFactory loggerFactory, IConfiguration configuration)
        {
            _options = options;
            _nameResolver = nameResolver;
            _rabbitMQServiceFactory = rabbitMQServiceFactory;
            _logger = loggerFactory?.CreateLogger(LogCategories.CreateTriggerCategory("RabbitMQ"));
            _configuration = configuration;
            _connectionParametersToService = new ConcurrentDictionary<string, IRabbitMQService>();
        }

        public void Initialize(ExtensionConfigContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            var rule = context.AddBindingRule<RabbitMQAttribute>();
            rule.AddValidator(ValidateBinding);
            rule.BindToCollector<byte[]>((attr) =>
            {
                return new RabbitMQAsyncCollector(CreateContext(attr), _logger);
            });
            rule.BindToInput<IModel>(new RabbitMQClientBuilder(this, _options));
            rule.AddConverter<string, byte[]>(msg => Encoding.UTF8.GetBytes(msg));
            rule.AddOpenConverter<OpenType.Poco, byte[]>(typeof(PocoToBytesConverter<>));

            var triggerRule = context.AddBindingRule<RabbitMQTriggerAttribute>();

            // More details about why the BindToTrigger was chosen instead of BindToTrigger<BasicDeliverEventArgs> detailed here https://github.com/Azure/azure-functions-rabbitmq-extension/issues/110
            triggerRule.BindToTrigger(new RabbitMQTriggerAttributeBindingProvider(
                    _nameResolver,
                    this,
                    _logger,
                    _options,
                    _configuration));
        }

        public void ValidateBinding(RabbitMQAttribute attribute, Type type)
        {
            string connectionString = Utility.FirstOrDefault(attribute.ConnectionStringSetting, _options.Value.ConnectionString);
            string hostName = Utility.FirstOrDefault(attribute.HostName, _options.Value.HostName) ?? Constants.LocalHost;
            _logger.LogInformation("Setting hostName to localhost since it was not specified");

            string userName = Utility.FirstOrDefault(attribute.UserName, _options.Value.UserName);
            string password = Utility.FirstOrDefault(attribute.Password, _options.Value.Password);

            if (string.IsNullOrEmpty(connectionString) && !Utility.ValidateUserNamePassword(userName, password, hostName))
            {
                throw new InvalidOperationException("RabbitMQ username and password required if not connecting to localhost");
            }
        }

        internal RabbitMQContext CreateContext(RabbitMQAttribute attribute)
        {
            string connectionString = Utility.FirstOrDefault(attribute.ConnectionStringSetting, _options.Value.ConnectionString);
            string hostName = Utility.FirstOrDefault(attribute.HostName, _options.Value.HostName);
            string queueName = Utility.FirstOrDefault(attribute.QueueName, _options.Value.QueueName);
            string userName = Utility.FirstOrDefault(attribute.UserName, _options.Value.UserName);
            string password = Utility.FirstOrDefault(attribute.Password, _options.Value.Password);
            int port = Utility.FirstOrDefault(attribute.Port, _options.Value.Port);

            RabbitMQAttribute resolvedAttribute;
            IRabbitMQService service;

            resolvedAttribute = new RabbitMQAttribute
            {
                ConnectionStringSetting = connectionString,
                HostName = hostName,
                QueueName = queueName,
                UserName = userName,
                Password = password,
                Port = port,
            };

            service = GetService(connectionString, hostName, queueName, userName, password, port);

            return new RabbitMQContext
            {
                ResolvedAttribute = resolvedAttribute,
                Service = service,
            };
        }

        internal IRabbitMQService GetService(string connectionString, string hostName, string queueName, string userName, string password, int port)
        {
            string[] keyArray = { connectionString, hostName, queueName, userName, password, port.ToString() };
            string key = string.Join(",", keyArray);
            return _connectionParametersToService.GetOrAdd(key, _ => _rabbitMQServiceFactory.CreateService(connectionString, hostName, queueName, userName, password, port));
        }

        // Overloaded method used only for getting the RabbitMQ client
        internal IRabbitMQService GetService(string connectionString, string hostName, string userName, string password, int port)
        {
            string[] keyArray = { connectionString, hostName, userName, password, port.ToString() };
            string key = string.Join(",", keyArray);
            return _connectionParametersToService.GetOrAdd(key, _ => _rabbitMQServiceFactory.CreateService(connectionString, hostName, userName, password, port));
        }
    }
}

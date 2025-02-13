// <copyright file="MeterProviderBuilderExtensionsTests.cs" company="OpenTelemetry Authors">
// Copyright The OpenTelemetry Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry.Resources;
using Xunit;

namespace OpenTelemetry.Metrics.Tests
{
    public class MeterProviderBuilderExtensionsTests
    {
        [Fact]
        public void ServiceLifecycleAvailableToSDKBuilderTest()
        {
            var builder = Sdk.CreateMeterProviderBuilder();

            builder.ConfigureServices(services => services.AddSingleton<MyInstrumentation>());

            MyInstrumentation myInstrumentation = null;

            RunBuilderServiceLifecycleTest(
                builder,
                () =>
                {
                    var provider = builder.Build() as MeterProviderSdk;

                    // Note: Build can only be called once
                    Assert.Throws<NotSupportedException>(() => builder.Build());

                    Assert.NotNull(provider);
                    Assert.NotNull(provider.OwnedServiceProvider);

                    myInstrumentation = ((IServiceProvider)provider.OwnedServiceProvider).GetRequiredService<MyInstrumentation>();

                    return provider;
                },
                provider =>
                {
                    provider.Dispose();
                });

            Assert.NotNull(myInstrumentation);
            Assert.True(myInstrumentation.Disposed);
        }

        [Fact]
        public void ServiceLifecycleAvailableToServicesBuilderTest()
        {
            var services = new ServiceCollection();

            bool testRun = false;

            ServiceProvider serviceProvider = null;
            MeterProviderSdk provider = null;

            services.ConfigureOpenTelemetryMetrics(builder =>
            {
                testRun = true;

                RunBuilderServiceLifecycleTest(
                    builder,
                    () =>
                    {
                        // Note: Build can't be called directly on builder tied to external services
                        Assert.Throws<NotSupportedException>(() => builder.Build());

                        serviceProvider = services.BuildServiceProvider();

                        provider = serviceProvider.GetRequiredService<MeterProvider>() as MeterProviderSdk;

                        Assert.NotNull(provider);
                        Assert.Null(provider.OwnedServiceProvider);

                        return provider;
                    },
                    (provider) => { });
            });

            Assert.True(testRun);

            Assert.NotNull(serviceProvider);
            Assert.NotNull(provider);

            Assert.False(provider.Disposed);

            serviceProvider.Dispose();

            Assert.True(provider.Disposed);
        }

        [Fact]
        public void SingleProviderForServiceCollectionTest()
        {
            var services = new ServiceCollection();

            services.ConfigureOpenTelemetryMetrics(builder =>
            {
                builder.AddInstrumentation<MyInstrumentation>(() => new());
            });

            services.ConfigureOpenTelemetryMetrics(builder =>
            {
                builder.AddInstrumentation<MyInstrumentation>(() => new());
            });

            using var serviceProvider = services.BuildServiceProvider();

            Assert.NotNull(serviceProvider);

            var meterProviders = serviceProvider.GetServices<MeterProvider>();

            Assert.Single(meterProviders);

            var provider = meterProviders.First() as MeterProviderSdk;

            Assert.NotNull(provider);

            Assert.Equal(2, provider.Instrumentations.Count);
        }

        [Fact]
        public void AddReaderUsingDependencyInjectionTest()
        {
            var builder = Sdk.CreateMeterProviderBuilder();

            builder.AddReader<MyReader>();
            builder.AddReader<MyReader>();

            using var provider = builder.Build() as MeterProviderSdk;

            Assert.NotNull(provider);

            var readers = ((IServiceProvider)provider.OwnedServiceProvider).GetServices<MyReader>();

            // Note: Two "Add" calls but it is a singleton so only a single registration is produced
            Assert.Single(readers);

            var reader = provider.Reader as CompositeMetricReader;

            Assert.NotNull(reader);

            // Note: Two "Add" calls due yield two readers added to provider, even though they are the same
            Assert.True(reader.Head.Value is MyReader);
            Assert.True(reader.Head.Next?.Value is MyReader);
        }

        [Fact]
        public void SetAndConfigureResourceTest()
        {
            var builder = Sdk.CreateMeterProviderBuilder();

            int configureInvocations = 0;

            builder.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("Test"));
            builder.ConfigureResource(builder =>
            {
                configureInvocations++;

                Assert.Single(builder.Resources);

                builder.AddAttributes(new Dictionary<string, object>() { ["key1"] = "value1" });

                Assert.Equal(2, builder.Resources.Count);
            });
            builder.SetResourceBuilder(ResourceBuilder.CreateEmpty());
            builder.ConfigureResource(builder =>
            {
                configureInvocations++;

                Assert.Empty(builder.Resources);

                builder.AddAttributes(new Dictionary<string, object>() { ["key2"] = "value2" });

                Assert.Single(builder.Resources);
            });

            using var provider = builder.Build() as MeterProviderSdk;

            Assert.Equal(2, configureInvocations);

            Assert.Single(provider.Resource.Attributes);
            Assert.Contains(provider.Resource.Attributes, kvp => kvp.Key == "key2" && (string)kvp.Value == "value2");
        }

        private static void RunBuilderServiceLifecycleTest(
            MeterProviderBuilder builder,
            Func<MeterProviderSdk> buildFunc,
            Action<MeterProviderSdk> postAction)
        {
            var baseBuilder = builder as MeterProviderBuilderBase;
            Assert.Null(baseBuilder.State);

            builder.AddMeter("TestSource");

            bool configureServicesCalled = false;
            builder.ConfigureServices(services =>
            {
                configureServicesCalled = true;

                Assert.NotNull(services);

                services.TryAddSingleton<MyReader>();

                services.ConfigureOpenTelemetryMetrics(b =>
                {
                    // Note: This is strange to call ConfigureOpenTelemetryMetrics here, but supported
                    b.AddInstrumentation<MyInstrumentation>();
                });
            });

            int configureBuilderInvocations = 0;
            builder.ConfigureBuilder((sp, builder) =>
            {
                configureBuilderInvocations++;

                var baseBuilder = builder as MeterProviderBuilderBase;
                Assert.NotNull(baseBuilder?.State);

                builder.AddMeter("TestSource2");

                Assert.Contains(baseBuilder.State.MeterSources, s => s == "TestSource");
                Assert.Contains(baseBuilder.State.MeterSources, s => s == "TestSource2");

                // Note: Services can't be configured at this stage
                Assert.Throws<NotSupportedException>(
                    () => builder.ConfigureServices(services => services.TryAddSingleton<MeterProviderBuilderExtensionsTests>()));

                builder.AddReader(sp.GetRequiredService<MyReader>());

                builder.ConfigureBuilder((_, b) =>
                {
                    // Note: ConfigureBuilder calls can be nested, this is supported
                    configureBuilderInvocations++;

                    b.ConfigureBuilder((_, _) =>
                    {
                        configureBuilderInvocations++;
                    });
                });
            });

            var provider = buildFunc();

            Assert.True(configureServicesCalled);
            Assert.Equal(3, configureBuilderInvocations);

            Assert.Single(provider.Instrumentations);
            Assert.True(provider.Instrumentations[0] is MyInstrumentation);
            Assert.True(provider.Reader is MyReader);

            postAction(provider);
        }

        private sealed class MyInstrumentation : IDisposable
        {
            internal bool Disposed;

            public void Dispose()
            {
                this.Disposed = true;
            }
        }

        private sealed class MyReader : MetricReader
        {
        }

        private sealed class MyExporter : BaseExporter<Metric>
        {
            public override ExportResult Export(in Batch<Metric> batch)
            {
                return ExportResult.Success;
            }
        }
    }
}

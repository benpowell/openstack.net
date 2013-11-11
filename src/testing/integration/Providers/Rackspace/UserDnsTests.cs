﻿namespace Net.OpenStack.Testing.Integration.Providers.Rackspace
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using JSIStudios.SimpleRESTServices.Client;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using net.openstack.Core;
    using net.openstack.Core.Domain;
    using net.openstack.Core.Providers;
    using net.openstack.Providers.Rackspace;
    using net.openstack.Providers.Rackspace.Objects.Dns;
    using Newtonsoft.Json;
    using Path = System.IO.Path;

    [TestClass]
    public class UserDnsTests
    {
        /// <summary>
        /// Users created by these unit tests have a username with this prefix, allowing
        /// them to be identified and cleaned up following a failed test.
        /// </summary>
        private static readonly string TestDomainPrefix = "UnitTestDomain-";

        /// <summary>
        /// This method can be used to clean up domains created during integration testing.
        /// </summary>
        /// <remarks>
        /// The Cloud DNS integration tests generally delete domains created during the
        /// tests, but test failures may lead to unused domains gathering on the system.
        /// This method searches for all domains matching the "integration testing"
        /// pattern (i.e., domains whose name starts with <see cref="TestDomainPrefix"/>),
        /// and attempts to delete them.
        /// <para>
        /// The deletion requests are sent in parallel, so a single deletion failure will
        /// not prevent the method from cleaning up other queues that can be successfully
        /// deleted. Note that the system does not increase the
        /// <see cref="ProviderBase{TProvider}.ConnectionLimit"/>, so the underlying REST
        /// requests may be pipelined if the number of domains to delete exceeds the
        /// default system connection limit.
        /// </para>
        /// </remarks>
        [TestMethod]
        [TestCategory(TestCategories.Cleanup)]
        public void CleanupTestDomains()
        {
            const int BatchSize = 10;

            IDnsService provider = CreateProvider();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TestTimeout(TimeSpan.FromSeconds(60))))
            {
                DnsDomain[] allDomains = ListAllDomains(provider, null, null, cancellationTokenSource.Token).Where(i => i.Name.StartsWith(TestDomainPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();

                List<Task> deleteTasks = new List<Task>();
                for (int i = 0; i < allDomains.Length; i += BatchSize)
                {
                    for (int j = i; j < i + BatchSize && j < allDomains.Length; j++)
                        Console.WriteLine("Deleting domain: {0}", allDomains[j].Name);

                    deleteTasks.Add(provider.RemoveDomainsAsync(allDomains.Skip(i).Take(BatchSize).Select(domain => domain.Id), true, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null));
                }

                Task.WaitAll(deleteTasks.ToArray());
            }
        }

        [TestMethod]
        [TestCategory(TestCategories.User)]
        [TestCategory(TestCategories.Dns)]
        public async Task TestListLimits()
        {
            IDnsService provider = CreateProvider();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TestTimeout(TimeSpan.FromSeconds(10))))
            {
                DnsServiceLimits limits = await provider.ListLimitsAsync(cancellationTokenSource.Token);
                Assert.IsNotNull(limits);
                Assert.IsNotNull(limits.RateLimits);
                Assert.IsNotNull(limits.AbsoluteLimits);

                Console.WriteLine(await JsonConvert.SerializeObjectAsync(limits, Formatting.Indented));
            }
        }

        [TestMethod]
        [TestCategory(TestCategories.User)]
        [TestCategory(TestCategories.Dns)]
        public async Task TestListLimitTypes()
        {
            IDnsService provider = CreateProvider();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TestTimeout(TimeSpan.FromSeconds(10))))
            {
                IEnumerable<LimitType> limitTypes = await provider.ListLimitTypesAsync(cancellationTokenSource.Token);
                Assert.IsNotNull(limitTypes);

                if (!limitTypes.Any())
                    Assert.Inconclusive("No limit types were returned by the server");

                foreach (var limitType in limitTypes)
                    Console.WriteLine(limitType.Name);

                Assert.IsTrue(limitTypes.Contains(LimitType.Rate));
                Assert.IsTrue(limitTypes.Contains(LimitType.Domain));
                Assert.IsTrue(limitTypes.Contains(LimitType.DomainRecord));
            }
        }

        [TestMethod]
        [TestCategory(TestCategories.User)]
        [TestCategory(TestCategories.Dns)]
        public async Task TestListSpecificLimit()
        {
            IDnsService provider = CreateProvider();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TestTimeout(TimeSpan.FromSeconds(10))))
            {
                IEnumerable<LimitType> limitTypes = await provider.ListLimitTypesAsync(cancellationTokenSource.Token);
                Assert.IsNotNull(limitTypes);

                if (!limitTypes.Any())
                    Assert.Inconclusive("No limit types were returned by the server");

                foreach (var limitType in limitTypes)
                {
                    Console.WriteLine();
                    Console.WriteLine("Limit Type: {0}", limitType);
                    Console.WriteLine();
                    DnsServiceLimits limits = await provider.ListLimitsAsync(limitType, cancellationTokenSource.Token);
                    Console.WriteLine(await JsonConvert.SerializeObjectAsync(limits, Formatting.Indented));
                }
            }
        }

        [TestMethod]
        [TestCategory(TestCategories.User)]
        [TestCategory(TestCategories.Dns)]
        public async Task TestListDomains()
        {
            IDnsService provider = CreateProvider();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TestTimeout(TimeSpan.FromSeconds(30))))
            {
                DnsDomain[] domains = ListAllDomains(provider, null, null, cancellationTokenSource.Token).ToArray();
                Assert.IsNotNull(domains);

                if (!domains.Any())
                    Assert.Inconclusive("No domains were returned by the server");

                foreach (var domain in domains)
                {
                    Console.WriteLine();
                    Console.WriteLine("Domain: {0} ({1})", domain.Name, domain.Id);
                    Console.WriteLine();
                    Console.WriteLine(await JsonConvert.SerializeObjectAsync(domain, Formatting.Indented));
                }
            }
        }

        /// <summary>
        /// This tests the behavior of the <see cref="IDnsService.CreateDomainsAsync"/> and
        /// <see cref="IDnsService.RemoveDomainsAsync"/> when performed on a single domain.
        /// </summary>
        /// <returns>A <see cref="Task"/> object representing the asynchronous operation.</returns>
        [TestMethod]
        [TestCategory(TestCategories.User)]
        [TestCategory(TestCategories.Dns)]
        public async Task TestCreateDomain()
        {
            string domainName = CreateRandomDomainName();

            IDnsService provider = CreateProvider();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TestTimeout(TimeSpan.FromSeconds(60))))
            {
                DnsConfiguration configuration = new DnsConfiguration(
                    new DnsDomainConfiguration(
                        name: domainName,
                        timeToLive: default(TimeSpan?),
                        emailAddress: "admin@" + domainName,
                        comment: "Integration test domain",
                        records: new DnsDomainRecordConfiguration[] { },
                        subdomains: new DnsSubdomainConfiguration[] { }));

                DnsJob<DnsDomains> createResponse = await provider.CreateDomainsAsync(configuration, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                IEnumerable<DnsDomain> createdDomains = Enumerable.Empty<DnsDomain>();
                if (createResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(createResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail();
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(createResponse.Response, Formatting.Indented));
                    createdDomains = createResponse.Response.Domains;
                }

                DnsDomain[] domains = ListAllDomains(provider, domainName, null, cancellationTokenSource.Token).ToArray();
                Assert.IsNotNull(domains);

                if (!domains.Any())
                    Assert.Inconclusive("No domains were returned by the server");

                foreach (var domain in domains)
                {
                    Console.WriteLine();
                    Console.WriteLine("Domain: {0} ({1})", domain.Name, domain.Id);
                    Console.WriteLine();
                    Console.WriteLine(await JsonConvert.SerializeObjectAsync(domain, Formatting.Indented));
                }

                DnsJob deleteResponse = await provider.RemoveDomainsAsync(createdDomains.Select(i => i.Id), false, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (deleteResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(deleteResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail("Failed to delete temporary domain created during the integration test.");
                }
            }
        }

        /// <summary>
        /// This tests the behavior of the <see cref="IDnsService.UpdateDomainsAsync"/> when performed
        /// on a single domain.
        /// </summary>
        /// <returns>A <see cref="Task"/> object representing the asynchronous operation.</returns>
        [TestMethod]
        [TestCategory(TestCategories.User)]
        [TestCategory(TestCategories.Dns)]
        public async Task TestUpdateDomain()
        {
            string domainName = CreateRandomDomainName();

            IDnsService provider = CreateProvider();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TestTimeout(TimeSpan.FromSeconds(60))))
            {
                DnsConfiguration configuration = new DnsConfiguration(
                    new DnsDomainConfiguration(
                        name: domainName,
                        timeToLive: default(TimeSpan?),
                        emailAddress: "admin@" + domainName,
                        comment: "Integration test domain",
                        records: new DnsDomainRecordConfiguration[] { },
                        subdomains: new DnsSubdomainConfiguration[] { }));

                DnsJob<DnsDomains> createResponse = await provider.CreateDomainsAsync(configuration, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                IEnumerable<DnsDomain> createdDomains = Enumerable.Empty<DnsDomain>();
                if (createResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(createResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail();
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(createResponse.Response, Formatting.Indented));
                    createdDomains = createResponse.Response.Domains;
                }

                DnsDomain[] domains = ListAllDomains(provider, domainName, null, cancellationTokenSource.Token).ToArray();
                Assert.IsNotNull(domains);

                if (!domains.Any())
                    Assert.Inconclusive("No domains were returned by the server");

                DnsUpdateConfiguration updateConfiguration = new DnsUpdateConfiguration(
                    new DnsDomainUpdateConfiguration(domains[0], comment: "Integration test domain 2"));
                await provider.UpdateDomainsAsync(updateConfiguration, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);

                DnsJob deleteResponse = await provider.RemoveDomainsAsync(createdDomains.Select(i => i.Id), false, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (deleteResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(deleteResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail("Failed to delete temporary domain created during the integration test.");
                }
            }
        }

        /// <summary>
        /// This tests the behavior of the <see cref="IDnsService.CloneDomainsAsync"/>.
        /// </summary>
        /// <returns>A <see cref="Task"/> object representing the asynchronous operation.</returns>
        [TestMethod]
        [TestCategory(TestCategories.User)]
        [TestCategory(TestCategories.Dns)]
        public async Task TestCloneDomain()
        {
            string domainName = CreateRandomDomainName();
            string clonedName = CreateRandomDomainName();

            IDnsService provider = CreateProvider();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TestTimeout(TimeSpan.FromSeconds(60))))
            {
                List<DomainId> domainsToRemove = new List<DomainId>();

                DnsConfiguration configuration = new DnsConfiguration(
                    new DnsDomainConfiguration(
                        name: domainName,
                        timeToLive: default(TimeSpan?),
                        emailAddress: "admin@" + domainName,
                        comment: "Integration test domain",
                        records: new DnsDomainRecordConfiguration[] { },
                        subdomains: new DnsSubdomainConfiguration[] { }));

                DnsJob<DnsDomains> createResponse = await provider.CreateDomainsAsync(configuration, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (createResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(createResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail();
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(createResponse.Response, Formatting.Indented));
                    domainsToRemove.AddRange(createResponse.Response.Domains.Select(i => i.Id));
                }

                DnsJob<DnsDomains> cloneResponse = await provider.CloneDomainAsync(createResponse.Response.Domains[0].Id, clonedName, true, true, true, true, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (cloneResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(cloneResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail();
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(cloneResponse.Response, Formatting.Indented));
                    domainsToRemove.AddRange(cloneResponse.Response.Domains.Select(i => i.Id));
                }

                DnsJob deleteResponse = await provider.RemoveDomainsAsync(domainsToRemove, false, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (deleteResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(deleteResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail("Failed to delete temporary domain created during the integration test.");
                }
            }
        }

        /// <summary>
        /// This tests the behavior of the <see cref="IDnsService.ExportDomainAsync"/> and <see cref="IDnsService.ImportDomainAsync"/>.
        /// </summary>
        /// <returns>A <see cref="Task"/> object representing the asynchronous operation.</returns>
        [TestMethod]
        [TestCategory(TestCategories.User)]
        [TestCategory(TestCategories.Dns)]
        public async Task TestDomainExportImport()
        {
            string domainName = CreateRandomDomainName();

            IDnsService provider = CreateProvider();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TestTimeout(TimeSpan.FromSeconds(60))))
            {
                List<DomainId> domainsToRemove = new List<DomainId>();

                DnsConfiguration configuration = new DnsConfiguration(
                    new DnsDomainConfiguration(
                        name: domainName,
                        timeToLive: default(TimeSpan?),
                        emailAddress: "admin@" + domainName,
                        comment: "Integration test domain",
                        records: new DnsDomainRecordConfiguration[] { },
                        subdomains: new DnsSubdomainConfiguration[] { }));

                DnsJob<DnsDomains> createResponse = await provider.CreateDomainsAsync(configuration, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (createResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(createResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail("Failed to create a test domain.");
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(createResponse.Response, Formatting.Indented));
                    domainsToRemove.AddRange(createResponse.Response.Domains.Select(i => i.Id));
                }

                DnsJob<ExportedDomain> exportedDomain = await provider.ExportDomainAsync(createResponse.Response.Domains[0].Id, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (exportedDomain.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(exportedDomain.Error.ToString(Formatting.Indented));
                    Assert.Fail("Failed to export test domain.");
                }

                Assert.AreEqual(DnsJobStatus.Completed, exportedDomain.Status);
                Assert.IsNotNull(exportedDomain.Response);

                Console.WriteLine("Exported domain:");
                Console.WriteLine(JsonConvert.SerializeObject(exportedDomain.Response, Formatting.Indented));
                Console.WriteLine();
                Console.WriteLine("Formatted exported output:");
                Console.WriteLine(exportedDomain.Response.Contents);

                Assert.IsNotNull(exportedDomain.Response.Id);
                Assert.IsFalse(string.IsNullOrEmpty(exportedDomain.Response.AccountId));
                Assert.IsFalse(string.IsNullOrEmpty(exportedDomain.Response.Contents));
                Assert.IsNotNull(exportedDomain.Response.ContentType);

                DnsJob deleteResponse = await provider.RemoveDomainsAsync(domainsToRemove, false, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (deleteResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(deleteResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail("Failed to delete temporary domain created during the integration test.");
                }

                domainsToRemove.Clear();

                SerializedDomain serializedDomain =
                    new SerializedDomain(
                        RemoveDefaultNameserverEntries(exportedDomain.Response.Contents),
                        exportedDomain.Response.ContentType);
                DnsJob<DnsDomains> importedDomain = await provider.ImportDomainAsync(new[] { serializedDomain }, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (importedDomain.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(importedDomain.Error.ToString(Formatting.Indented));
                    Assert.Fail("Failed to import the test domain.");
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(importedDomain.Response, Formatting.Indented));
                    domainsToRemove.AddRange(importedDomain.Response.Domains.Select(i => i.Id));
                }

                deleteResponse = await provider.RemoveDomainsAsync(domainsToRemove, false, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (deleteResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(deleteResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail("Failed to delete temporary domain created during the integration test.");
                }
            }
        }

        /// <summary>
        /// This method removes the default NS entries from an exported domain in BIND 9 format.
        /// Attempting to import a domain containing these entries results in an error due to
        /// conflicting DNS entries.
        /// </summary>
        /// <param name="bind9Content">The exported DNS domain in BIND 9 format, which may contain default NS records.</param>
        /// <returns>The serialized domain with the default NS records removed.</returns>
        private static string RemoveDefaultNameserverEntries(string bind9Content)
        {
            Regex expression = new Regex(@"\n.*\tNS\tdns[12]\.stabletransit\.com.*");
            return expression.Replace(bind9Content, string.Empty);
        }

        [TestMethod]
        [TestCategory(TestCategories.User)]
        [TestCategory(TestCategories.Dns)]
        public async Task TestCreateSubdomain()
        {
            string domainName = CreateRandomDomainName();
            string subdomainName = "www";

            IDnsService provider = CreateProvider();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TestTimeout(TimeSpan.FromSeconds(60))))
            {
                DnsConfiguration configuration = new DnsConfiguration(
                    new DnsDomainConfiguration(
                        name: domainName,
                        timeToLive: default(TimeSpan?),
                        emailAddress: "admin@" + domainName,
                        comment: "Integration test domain",
                        records: new DnsDomainRecordConfiguration[] { },
                        subdomains: new DnsSubdomainConfiguration[]
                            {
                                new DnsSubdomainConfiguration(
                                    emailAddress: string.Format("sub-admin@{0}.{1}", subdomainName, domainName),
                                    name: string.Format("{0}.{1}", subdomainName, domainName),
                                    comment: "Integration test subdomain")
                            }));

                DnsJob<DnsDomains> createResponse = await provider.CreateDomainsAsync(configuration, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                IEnumerable<DnsDomain> createdDomains = Enumerable.Empty<DnsDomain>();
                if (createResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(createResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail();
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(createResponse.Response, Formatting.Indented));
                    createdDomains = createResponse.Response.Domains;
                }

                DnsDomain[] domains = ListAllDomains(provider, domainName, null, cancellationTokenSource.Token).ToArray();
                Assert.IsNotNull(domains);

                if (!domains.Any())
                    Assert.Inconclusive("No domains were returned by the server");

                foreach (var domain in domains)
                {
                    Console.WriteLine();
                    Console.WriteLine("Domain: {0} ({1})", domain.Name, domain.Id);
                    Console.WriteLine();
                    Console.WriteLine(await JsonConvert.SerializeObjectAsync(domain, Formatting.Indented));
                }

                DomainId domainId = createResponse.Response.Domains[0].Id;
                DnsSubdomain[] subdomains = ListAllSubdomains(provider, domainId, null, cancellationTokenSource.Token).ToArray();
                Assert.IsNotNull(subdomains);
                Assert.AreEqual(1, subdomains.Length);
                foreach (var subdomain in subdomains)
                {
                    Console.WriteLine();
                    Console.WriteLine("Subdomain: {0} ({1})", subdomain.Name, subdomain.Id);
                    Console.WriteLine();
                    Console.WriteLine(await JsonConvert.SerializeObjectAsync(subdomain, Formatting.Indented));
                }

                DnsJob deleteResponse = await provider.RemoveDomainsAsync(createdDomains.Select(i => i.Id), true, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (deleteResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(deleteResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail("Failed to delete temporary domain created during the integration test.");
                }
            }
        }

        [TestMethod]
        [TestCategory(TestCategories.User)]
        [TestCategory(TestCategories.Dns)]
        public async Task TestCreateRecords()
        {
            string domainName = CreateRandomDomainName();

            IDnsService provider = CreateProvider();
            using (CancellationTokenSource cancellationTokenSource = new CancellationTokenSource(TestTimeout(TimeSpan.FromSeconds(60))))
            {
                DnsConfiguration configuration = new DnsConfiguration(
                    new DnsDomainConfiguration(
                        name: domainName,
                        timeToLive: default(TimeSpan?),
                        emailAddress: "admin@" + domainName,
                        comment: "Integration test domain",
                        records: new DnsDomainRecordConfiguration[] { },
                        subdomains: new DnsSubdomainConfiguration[] { }));

                DnsJob<DnsDomains> createResponse = await provider.CreateDomainsAsync(configuration, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                IEnumerable<DnsDomain> createdDomains = Enumerable.Empty<DnsDomain>();
                if (createResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(createResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail();
                }
                else
                {
                    Console.WriteLine(JsonConvert.SerializeObject(createResponse.Response, Formatting.Indented));
                    createdDomains = createResponse.Response.Domains;
                }

                DnsDomain[] domains = ListAllDomains(provider, domainName, null, cancellationTokenSource.Token).ToArray();
                Assert.IsNotNull(domains);
                Assert.AreEqual(1, domains.Length);

                foreach (var domain in domains)
                {
                    Console.WriteLine();
                    Console.WriteLine("Domain: {0} ({1})", domain.Name, domain.Id);
                    Console.WriteLine();
                    Console.WriteLine(await JsonConvert.SerializeObjectAsync(domain, Formatting.Indented));
                }

                DomainId domainId = createResponse.Response.Domains[0].Id;
                DnsDomainRecordConfiguration[] recordConfigurations =
                    {
                        new DnsDomainRecordConfiguration(
                            type: DnsRecordType.A,
                            name: string.Format("www.{0}", domainName),
                            data: "127.0.0.1",
                            timeToLive: null,
                            comment: "Integration test record",
                            priority: null)

                    };
                DnsJob<DnsRecordsList> recordsResponse = await provider.AddRecordsAsync(domainId, recordConfigurations, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                Assert.IsNotNull(recordsResponse.Response);
                Assert.IsNotNull(recordsResponse.Response.Records);
                DnsRecord[] records = recordsResponse.Response.Records.ToArray();

                foreach (var record in records)
                {
                    Console.WriteLine();
                    Console.WriteLine("Record: {0} ({1})", record.Name, record.Id);
                    Console.WriteLine();
                    Console.WriteLine(await JsonConvert.SerializeObjectAsync(record, Formatting.Indented));
                }

                await provider.UpdateRecordsAsync(domainId, new[] { new DnsDomainRecordUpdateConfiguration(records[0], records[0].Name, comment: "Integration test record 2") }, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);

                DnsJob deleteResponse = await provider.RemoveDomainsAsync(createdDomains.Select(i => i.Id), false, AsyncCompletionOption.RequestCompleted, cancellationTokenSource.Token, null);
                if (deleteResponse.Status == DnsJobStatus.Error)
                {
                    Console.WriteLine(deleteResponse.Error.ToString(Formatting.Indented));
                    Assert.Fail("Failed to delete temporary domain created during the integration test.");
                }
            }
        }

        /// <summary>
        /// Gets all existing domains through a series of asynchronous operations,
        /// each of which requests a subset of the available domains.
        /// </summary>
        /// <remarks>
        /// Each of the returned tasks is executed asynchronously but sequentially. This
        /// method will not send concurrent requests to the DNS service.
        /// <para>
        /// Due to the way the list end is detected, the final task may return an empty
        /// collection of <see cref="DnsDomain"/> instances.
        /// </para>
        /// </remarks>
        /// <param name="provider">The DNS service.</param>
        /// <param name="limit">The maximum number of <see cref="DnsDomain"/> objects to return from a single task. If this value is <c>null</c>, a provider-specific default is used.</param>
        /// <param name="detailed"><c>true</c> to return detailed information for each domain; otherwise, <c>false</c>.</param>
        /// <returns>
        /// A collections of <see cref="Task{TResult}"/> objects, each of which
        /// represents an asynchronous operation to gather a subset of the available
        /// domains.
        /// </returns>
        /// <exception cref="ArgumentNullException">If <paramref name="provider"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">If <paramref name="limit"/> is less than or equal to 0.</exception>
        private static IEnumerable<DnsDomain> ListAllDomains(IDnsService provider, string domainName, int? limit, CancellationToken cancellationToken)
        {
            if (limit <= 0)
                throw new ArgumentOutOfRangeException("limit");

            int index = 0;
            int previousIndex;
            int? totalEntries = null;

            do
            {
                previousIndex = index;
                Task<Tuple<IEnumerable<DnsDomain>, int?>> domains = provider.ListDomainsAsync(domainName, index, limit, cancellationToken);
                totalEntries = domains.Result.Item2;
                foreach (DnsDomain domain in domains.Result.Item1)
                {
                    index++;
                    yield return domain;
                }

                if (limit == null)
                {
                    // this service will return a 400 error if offset is not a multiple of limit,
                    // or if the limit is not specified
                    limit = index;
                }
            } while (index > previousIndex && (totalEntries == null || index < totalEntries));
        }

        /// <summary>
        /// Gets all existing subdomains for a particular domain through a series of
        /// asynchronous operations, each of which requests a subset of the available
        /// subdomains.
        /// </summary>
        /// <remarks>
        /// Each of the returned tasks is executed asynchronously but sequentially. This
        /// method will not send concurrent requests to the DNS service.
        /// <para>
        /// Due to the way the list end is detected, the final task may return an empty
        /// collection of <see cref="DnsSubdomain"/> instances.
        /// </para>
        /// </remarks>
        /// <param name="provider">The DNS service.</param>
        /// <param name="limit">The maximum number of <see cref="DnsSubdomain"/> objects to return from a single task. If this value is <c>null</c>, a provider-specific default is used.</param>
        /// <param name="detailed"><c>true</c> to return detailed information for each subdomain; otherwise, <c>false</c>.</param>
        /// <returns>
        /// A collections of <see cref="Task{TResult}"/> objects, each of which
        /// represents an asynchronous operation to gather a subset of the available
        /// subdomains.
        /// </returns>
        private static IEnumerable<DnsSubdomain> ListAllSubdomains(IDnsService provider, DomainId domainId, int? limit, CancellationToken cancellationToken)
        {
            if (limit <= 0)
                throw new ArgumentOutOfRangeException("limit");

            int index = 0;
            int previousIndex;
            int? totalEntries = null;

            do
            {
                previousIndex = index;
                Task<Tuple<IEnumerable<DnsSubdomain>, int?>> subdomains = provider.ListSubdomainsAsync(domainId, index, limit, cancellationToken);
                totalEntries = subdomains.Result.Item2;
                foreach (DnsSubdomain subdomain in subdomains.Result.Item1)
                {
                    index++;
                    yield return subdomain;
                }

                if (limit == null)
                {
                    // this service will return a 400 error if offset is not a multiple of limit,
                    // or if the limit is not specified
                    limit = index;
                }
            } while (index > previousIndex && (totalEntries == null || index < totalEntries));
        }

        private TimeSpan TestTimeout(TimeSpan timeout)
        {
            if (Debugger.IsAttached)
                return TimeSpan.FromDays(1);

            return timeout;
        }

        private string CreateRandomDomainName()
        {
            return TestDomainPrefix + Path.GetRandomFileName().Replace('.', '-') + ".com";
        }

        private IDnsService CreateProvider()
        {
            CloudDnsProvider provider = new TestCloudDnsProvider(Bootstrapper.Settings.TestIdentity, Bootstrapper.Settings.DefaultRegion, false, null, null);
            provider.BeforeAsyncWebRequest +=
                (sender, e) =>
                {
                    Console.WriteLine("{0} (Request) {1} {2}", DateTime.Now, e.Request.Method, e.Request.RequestUri);
                };
            provider.AfterAsyncWebResponse +=
                (sender, e) =>
                {
                    Console.WriteLine("{0} (Result {1}) {2}", DateTime.Now, e.Response.StatusCode, e.Response.ResponseUri);
                };

            return provider;
        }

        private class TestCloudDnsProvider : CloudDnsProvider
        {
            public TestCloudDnsProvider(CloudIdentity defaultIdentity, string defaultRegion, bool internalUrl, IIdentityProvider identityProvider, IRestService restService)
                : base(defaultIdentity, defaultRegion, internalUrl, identityProvider, restService)
            {
            }

            protected override Tuple<HttpWebResponse, string> ReadResultImpl(Task<WebResponse> task, CancellationToken cancellationToken)
            {
                Tuple<HttpWebResponse, string> result = base.ReadResultImpl(task, cancellationToken);
                foreach (string header in result.Item1.Headers)
                {
                    Console.WriteLine(string.Format("{0}: {1}", header, result.Item1.Headers[header]));
                }

                if (!string.IsNullOrEmpty(result.Item2))
                {
                    object parsed = JsonConvert.DeserializeObject(result.Item2);
                    Console.WriteLine(JsonConvert.SerializeObject(parsed, Formatting.Indented));
                }

                return result;
            }
        }
    }
}
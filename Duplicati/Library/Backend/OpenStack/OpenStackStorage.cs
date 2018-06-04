﻿//  Copyright (C) 2015, The Duplicati Team
//  http://www.duplicati.com, info@duplicati.com
//
//  This library is free software; you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as
//  published by the Free Software Foundation; either version 2.1 of the
//  License, or (at your option) any later version.
//
//  This library is distributed in the hope that it will be useful, but
//  WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//  Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public
//  License along with this library; if not, write to the Free Software
//  Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
using System;
using System.Collections.Generic;
using Duplicati.Library.Interface;
using System.Linq;
using System.IO;
using Duplicati.Library.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Duplicati.Library.Strings;
using System.Net;
using System.Text;

namespace Duplicati.Library.Backend.OpenStack
{
    public class OpenStackStorage : IBackend, IStreamingBackend
    {
        private const string DOMAINNAME_OPTION = "openstack-domain-name";
        private const string USERNAME_OPTION = "auth-username";
        private const string PASSWORD_OPTION = "auth-password";
        private const string TENANTNAME_OPTION = "openstack-tenant-name";
        private const string AUTHURI_OPTION = "openstack-authuri";
        private const string VERSION_OPTION = "openstack-version";
        private const string APIKEY_OPTION = "openstack-apikey";
        private const string REGION_OPTION = "openstack-region";

        private const int PAGE_LIMIT = 500;


        private readonly string m_container;
        private readonly string m_prefix;

        private readonly string m_domainName;
        private readonly string m_username;
        private readonly string m_password;
        private readonly string m_authUri;
        private readonly string m_version;
        private readonly string m_tenantName;
        private readonly string m_apikey;
        private readonly string m_region;

        protected string m_simplestorageendpoint;

        private readonly WebHelper m_helper;
        private OpenStackAuthResponse.TokenClass m_accessToken;

        public static readonly KeyValuePair<string, string>[] KNOWN_OPENSTACK_PROVIDERS = {
            new KeyValuePair<string, string>("Rackspace US", "https://identity.api.rackspacecloud.com/v2.0"),
            new KeyValuePair<string, string>("Rackspace UK", "https://lon.identity.api.rackspacecloud.com/v2.0"),
            new KeyValuePair<string, string>("OVH Cloud Storage", "https://auth.cloud.ovh.net/v2.0"),
            new KeyValuePair<string, string>("Selectel Cloud Storage", "https://auth.selcdn.ru"),
            new KeyValuePair<string, string>("Memset Cloud Storage", "https://auth.storage.memset.com"),
        };

        public static readonly KeyValuePair<string, string>[] OPENSTACK_VERSIONS = {
            new KeyValuePair<string, string>("v2.0", "v2"),
            new KeyValuePair<string, string>("v3", "v3"),
        };


        private class Keystone3AuthRequest
        {
            public class AuthContainer
            {
                public Identity identity { get; set; }
                public Scope scope { get; set; }
            }

            public class Identity
            {
                [JsonProperty(ItemConverterType=typeof(StringEnumConverter))]
                public IdentityMethods[] methods { get; set; }

                [JsonProperty("password", NullValueHandling = NullValueHandling.Ignore)]
                public PasswordBasedRequest PasswordCredentials { get; set; }

                public Identity()
                {
                    this.methods = new [] { IdentityMethods.password };
                }
            }

            public class Scope
            {
                public Project project;
            }

            public enum IdentityMethods
            {
                password,
            }

            public class PasswordBasedRequest
            {
                public UserCredentials user { get; set; }
            }

            public class UserCredentials
            {
                public Domain domain { get; set; }
                public string name { get; set; }
                public string password { get; set; }

                public UserCredentials()
                {
                }
                public UserCredentials(Domain domain, string name, string password)
                {
                    this.domain = domain;
                    this.name = name;
                    this.password = password;
                }

            }

            public class Domain
            {
                public string name { get; set; }

                public Domain(string name)
                {
                    this.name = name;
                }
            }

            public class Project {
                public Domain domain { get; set; }
                public string name { get; set; }

                public Project(Domain domain, string name)
                {
                    this.domain = domain;
                    this.name = name;
                }
            }

            public AuthContainer auth { get; set; }

            public Keystone3AuthRequest(string domain_name, string username, string password, string project_name)
            {
                Domain domain = new Domain(domain_name);

                this.auth = new AuthContainer();
                this.auth.identity = new Identity();
                this.auth.identity.PasswordCredentials = new PasswordBasedRequest();
                this.auth.identity.PasswordCredentials.user = new UserCredentials(domain,username,password);
                this.auth.scope = new Scope();
                this.auth.scope.project = new Project(domain, project_name);
            }
        }

        private class OpenStackAuthRequest
        {
            public class AuthContainer
            {
                [JsonProperty("RAX-KSKEY:apiKeyCredentials", NullValueHandling = NullValueHandling.Ignore)]
                public ApiKeyBasedRequest ApiCredentials { get; set; }

                [JsonProperty("passwordCredentials", NullValueHandling = NullValueHandling.Ignore)]
                public PasswordBasedRequest PasswordCredentials { get; set; }

                [JsonProperty("tenantName", NullValueHandling = NullValueHandling.Ignore)]
                public string TenantName { get; set; }

                [JsonProperty("token", NullValueHandling = NullValueHandling.Ignore)]
                public TokenBasedRequest Token { get; set; }

            }

            public class ApiKeyBasedRequest
            {
                public string username { get; set; }
                public string apiKey { get; set; }
            }

            public class PasswordBasedRequest
            {
                public string username { get; set; }
                public string password { get; set; }
                public string tenantName { get; set; }
            }

            public class TokenBasedRequest
            {
                public string id { get; set; }
            }


            public AuthContainer auth { get; set; }

            public OpenStackAuthRequest(string tenantname, string username, string password, string apikey)
            {
                this.auth = new AuthContainer();
                this.auth.TenantName = tenantname;

                if (string.IsNullOrEmpty(apikey))
                {
                    this.auth.PasswordCredentials = new PasswordBasedRequest
                    {
                        username = username,
                        password = password,
                    };
                }
                else
                {
                    this.auth.ApiCredentials = new ApiKeyBasedRequest
                    {
                        username = username,
                        apiKey = apikey
                    };
                }

            }
        }

        private class Keystone3AuthResponse
        {
            public TokenClass token { get; set; }

            public class EndpointItem
            {
                // 'interface' is a reserved keyword, so we need this decorator to map it
                [JsonProperty(PropertyName = "interface")]
                public string interface_name { get; set; }
                public string region { get; set; }
                public string url { get; set; }
            }

            public class CatalogItem
            {
                public EndpointItem[] endpoints { get; set; }
                public string name { get; set; }
                public string type { get; set; }
            }
            public class TokenClass
            {
                public CatalogItem[] catalog { get; set; }
                public DateTime? expires_at { get; set; }
            }
        }

        private class OpenStackAuthResponse
        {
            public AccessClass access { get; set; }

            public class TokenClass
            {
                public string id { get; set; }
                public DateTime? expires { get; set; }
            }

            public class EndpointItem
            {
                public string region { get; set; }
                public string tenantId { get; set; }
                public string publicURL { get; set; }
                public string internalURL { get; set; }
            }

            public class ServiceItem
            {
                public string name { get; set; }
                public string type { get; set; }
                public EndpointItem[] endpoints { get; set; }
            }

            public class AccessClass
            {
                public TokenClass token { get; set; }
                public ServiceItem[] serviceCatalog { get; set; }
            }

        }

        private class OpenStackStorageItem
        {
            public string name { get; set; }
            public DateTime? last_modified { get; set; }
            public long? bytes { get; set; }
            public string content_type { get; set; }
            public string subdir { get; set; }
        }

        private class WebHelper : JSONWebHelper
        {
            private readonly OpenStackStorage m_parent;

            public WebHelper(OpenStackStorage parent) { m_parent = parent; }

            public override HttpWebRequest CreateRequest(string url, string method = null)
            {
                var req = base.CreateRequest(url, method);
                req.Headers["X-Auth-Token"] = m_parent.AccessToken;
                return req;
            }
        }

        public OpenStackStorage()
        {
        }

        public OpenStackStorage(string url, Dictionary<string, string> options)
        {
            var uri = new Utility.Uri(url);

            m_container = uri.Host;
            m_prefix = "/" + uri.Path;
            if (!m_prefix.EndsWith("/", StringComparison.Ordinal))
                m_prefix += "/";

            // For OpenStack we do not use a leading slash
            if (m_prefix.StartsWith("/", StringComparison.Ordinal))
                m_prefix = m_prefix.Substring(1);

            options.TryGetValue(DOMAINNAME_OPTION, out m_domainName);
            options.TryGetValue(USERNAME_OPTION, out m_username);
            options.TryGetValue(PASSWORD_OPTION, out m_password);
            options.TryGetValue(TENANTNAME_OPTION, out m_tenantName);
            options.TryGetValue(AUTHURI_OPTION, out m_authUri);
            options.TryGetValue(VERSION_OPTION, out m_version);
            options.TryGetValue(APIKEY_OPTION, out m_apikey);
            options.TryGetValue(REGION_OPTION, out m_region);

            if (string.IsNullOrWhiteSpace(m_username))
                throw new UserInformationException(Strings.OpenStack.MissingOptionError(USERNAME_OPTION), "OpenStackMissingUsername");
            if (string.IsNullOrWhiteSpace(m_authUri))
                throw new UserInformationException(Strings.OpenStack.MissingOptionError(AUTHURI_OPTION), "OpenStackMissingAuthUri");

            switch (m_version)
            {
                case "v3":
                    if (string.IsNullOrWhiteSpace(m_password))
                        throw new UserInformationException(Strings.OpenStack.MissingOptionError(PASSWORD_OPTION), "OpenStackMissingPassword");
                    if (string.IsNullOrWhiteSpace(m_domainName))
                        throw new UserInformationException(Strings.OpenStack.MissingOptionError(DOMAINNAME_OPTION), "OpenStackMissingDomainName");
                    if (string.IsNullOrWhiteSpace(m_tenantName))
                        throw new UserInformationException(Strings.OpenStack.MissingOptionError(TENANTNAME_OPTION), "OpenStackMissingTenantName");    
                    break;
                case "v2":
                default:
                    if (string.IsNullOrWhiteSpace(m_apikey))
                    {
                        if (string.IsNullOrWhiteSpace(m_password))
                            throw new UserInformationException(Strings.OpenStack.MissingOptionError(PASSWORD_OPTION), "OpenStackMissingPassword");
                        if (string.IsNullOrWhiteSpace(m_tenantName))
                            throw new UserInformationException(Strings.OpenStack.MissingOptionError(TENANTNAME_OPTION), "OpenStackMissingTenantName");
                    }
                    break;
            }

            m_helper = new WebHelper(this);
        }

        protected virtual string AccessToken
        {
            get
            {
                if (m_accessToken == null || (m_accessToken.expires.HasValue && (m_accessToken.expires.Value - DateTime.UtcNow).TotalSeconds < 30))
                    GetAuthResponse();
                
                return m_accessToken.id;                    
            }
        }

        private static string JoinUrls(string uri, string fragment)
        {
            fragment = fragment ?? "";
            return uri + (uri.EndsWith("/", StringComparison.Ordinal) ? "" : "/") + (fragment.StartsWith("/", StringComparison.Ordinal) ? fragment.Substring(1) : fragment);
        }
        private static string JoinUrls(string uri, string fragment1, string fragment2)
        {
            return JoinUrls(JoinUrls(uri, fragment1), fragment2);
        }

        private void GetAuthResponse()
        {
            switch (this.m_version)
            {
                case "v3":
                    GetKeystone3AuthResponse();
                    break;
                case "v2":
                default:
                    GetOpenstackAuthResponse();
                    break;
            }
        }

        private Keystone3AuthResponse GetKeystone3AuthResponse()
        {
            var helper = new JSONWebHelper();

            var req = helper.CreateRequest(JoinUrls(m_authUri, "auth/tokens"));
            req.Accept = "application/json";
            req.Method = "POST";

            var data = Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(
                    new Keystone3AuthRequest(m_domainName, m_username, m_password, m_tenantName)
                ));
            
            req.ContentLength = data.Length;
            req.ContentType = "application/json; charset=UTF-8";

            using (var rs = req.GetRequestStream())
                rs.Write(data, 0, data.Length);

            WebResponse http_response = req.GetResponse();

            Keystone3AuthResponse resp;
            using (var reader = new StreamReader(http_response.GetResponseStream()))
            {
                resp = Newtonsoft.Json.JsonConvert.DeserializeObject<Keystone3AuthResponse>(
                    reader.ReadToEnd());
            }

            string token = http_response.Headers["X-Subject-Token"];
            this.m_accessToken = new OpenStackAuthResponse.TokenClass();
            this.m_accessToken.id = token;
            this.m_accessToken.expires = resp.token.expires_at;

            // Grab the endpoint now that we have received it anyway
            var fileservice = resp.token.catalog.FirstOrDefault(x => string.Equals(x.type, "object-store", StringComparison.OrdinalIgnoreCase));
            if (fileservice == null)
                throw new Exception("No object-store service found, is this service supported by the provider?");
            
            var endpoint = fileservice.endpoints.FirstOrDefault(x => (string.Equals(m_region, x.region) && string.Equals(x.interface_name, "public", StringComparison.OrdinalIgnoreCase))) ?? fileservice.endpoints.First();
            m_simplestorageendpoint = endpoint.url;

            return resp;
        }

        private OpenStackAuthResponse GetOpenstackAuthResponse()
        {
            var helper = new JSONWebHelper();

            var req = helper.CreateRequest(JoinUrls(m_authUri, "tokens"));
            req.Accept = "application/json";
            req.Method = "POST";

            var resp = helper.ReadJSONResponse<OpenStackAuthResponse>(
                req,
                new OpenStackAuthRequest(m_tenantName, m_username, m_password, m_apikey)
            );

            m_accessToken = resp.access.token;

            // Grab the endpoint now that we have received it anyway
            var fileservice = resp.access.serviceCatalog.Where(x => string.Equals(x.type, "object-store", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (fileservice == null)
                throw new Exception("No object-store service found, is this service supported by the provider?");

            var endpoint = fileservice.endpoints.Where(x => string.Equals(m_region, x.region)).FirstOrDefault() ?? fileservice.endpoints.First();

            m_simplestorageendpoint = endpoint.publicURL;

            return resp;
        }

        protected virtual string SimpleStorageEndPoint
        {
            get
            {
                if (m_simplestorageendpoint == null)
                    GetAuthResponse();

                return m_simplestorageendpoint;
            }
        }

        #region IStreamingBackend implementation
        public void Put(string remotename, System.IO.Stream stream)
        {
            var url = JoinUrls(SimpleStorageEndPoint, m_container, Library.Utility.Uri.UrlPathEncode(m_prefix + remotename));
            using(m_helper.GetResponse(url, stream, "PUT"))
            { }
        }
        public void Get(string remotename, System.IO.Stream stream)
        {
            var url = JoinUrls(SimpleStorageEndPoint, m_container, Library.Utility.Uri.UrlPathEncode(m_prefix + remotename));

            try
            {
                using(var resp = m_helper.GetResponse(url))
                using(var rs = AsyncHttpRequest.TrySetTimeout(resp.GetResponseStream()))
                    Library.Utility.Utility.CopyStream(rs, stream);
            }
            catch(WebException wex)
            {
                if (wex.Response is HttpWebResponse && ((HttpWebResponse)wex.Response).StatusCode == HttpStatusCode.NotFound)
                    throw new FileMissingException();
                else
                    throw;
            }

        }
        #endregion
        #region IBackend implementation

        private T HandleListExceptions<T>(Func<T> func)
        {
            try
            {
                return func();
            }
            catch (WebException wex)
            {
                if (wex.Response is HttpWebResponse && (((HttpWebResponse)wex.Response).StatusCode == HttpStatusCode.NotFound))
                    throw new FolderMissingException();
                else
                    throw;
            }
        }

        public IEnumerable<IFileEntry> List()
        {
            var plainurl = JoinUrls(SimpleStorageEndPoint, m_container) + string.Format("?format=json&delimiter=/&limit={0}", PAGE_LIMIT);
            if (!string.IsNullOrEmpty(m_prefix))
                plainurl += "&prefix=" + Library.Utility.Uri.UrlEncode(m_prefix);

            var url = plainurl;

            while (true)
            {
                var req = m_helper.CreateRequest(url);
                req.Accept = "application/json";

                var items = HandleListExceptions(() => m_helper.ReadJSONResponse<OpenStackStorageItem[]>(req));
                foreach (var n in items)
                {
                    var name = n.name;
                    if (name.StartsWith(m_prefix, StringComparison.Ordinal))
                        name = name.Substring(m_prefix.Length);

                    if (n.bytes == null)
                        yield return new FileEntry(name);
                    else if (n.last_modified == null)
                        yield return new FileEntry(name, n.bytes.Value);
                    else
                        yield return new FileEntry(name, n.bytes.Value, n.last_modified.Value, n.last_modified.Value);
                }

                if (items.Length != PAGE_LIMIT)
                    yield break;

                // Prepare next listing entry
                url = plainurl + string.Format("&marker={0}", Library.Utility.Uri.UrlEncode(items.Last().name));
            }
        }

        public void Put(string remotename, string filename)
        {
            using (System.IO.FileStream fs = System.IO.File.OpenRead(filename))
                Put(remotename, fs);
        }

        public void Get(string remotename, string filename)
        {
            using (System.IO.FileStream fs = System.IO.File.Create(filename))
                Get(remotename, fs);
        }
        public void Delete(string remotename)
        {
            var url = JoinUrls(SimpleStorageEndPoint, m_container, Library.Utility.Uri.UrlPathEncode(m_prefix + remotename));
            m_helper.ReadJSONResponse<object>(url, null, "DELETE");
        }
        public void Test()
        {
            this.TestList();
        }
        public void CreateFolder()
        {
            var url = JoinUrls(SimpleStorageEndPoint, m_container);
            using(m_helper.GetResponse(url, null, "PUT"))
            { }
        }
        public string DisplayName
        {
            get
            {
                return Strings.OpenStack.DisplayName;
            }
        }
        public string ProtocolKey
        {
            get
            {
                return "openstack";
            }
        }
        public IList<ICommandLineArgument> SupportedCommands
        {
            get
            {
                var authuris = new StringBuilder();
                foreach(var s in KNOWN_OPENSTACK_PROVIDERS)
                    authuris.AppendLine(string.Format("{0}: {1}", s.Key, s.Value));

                return new List<ICommandLineArgument>(new [] {
                    new CommandLineArgument(DOMAINNAME_OPTION, CommandLineArgument.ArgumentType.String, Strings.OpenStack.DomainnameOptionShort, Strings.OpenStack.UsernameOptionLong),
                    new CommandLineArgument(USERNAME_OPTION, CommandLineArgument.ArgumentType.String, Strings.OpenStack.UsernameOptionShort, Strings.OpenStack.UsernameOptionLong),
                    new CommandLineArgument(PASSWORD_OPTION, CommandLineArgument.ArgumentType.Password, Strings.OpenStack.PasswordOptionShort, Strings.OpenStack.PasswordOptionLong(TENANTNAME_OPTION)),
                    new CommandLineArgument(TENANTNAME_OPTION, CommandLineArgument.ArgumentType.String, Strings.OpenStack.TenantnameOptionShort, Strings.OpenStack.TenantnameOptionLong),
                    new CommandLineArgument(APIKEY_OPTION, CommandLineArgument.ArgumentType.Password, Strings.OpenStack.ApikeyOptionShort, Strings.OpenStack.ApikeyOptionLong),
                    new CommandLineArgument(AUTHURI_OPTION, CommandLineArgument.ArgumentType.String, Strings.OpenStack.AuthuriOptionShort, Strings.OpenStack.AuthuriOptionLong(authuris.ToString())),
                    new CommandLineArgument(VERSION_OPTION, CommandLineArgument.ArgumentType.String, Strings.OpenStack.VersionOptionShort, Strings.OpenStack.AuthuriOptionLong(authuris.ToString())),
                    new CommandLineArgument(REGION_OPTION, CommandLineArgument.ArgumentType.String, Strings.OpenStack.RegionOptionShort, Strings.OpenStack.RegionOptionLong),
                });
            }
        }
        public string Description
        {
            get
            {
                return Strings.OpenStack.Description;
            }
        }

        public virtual string[] DNSName
        {
            get 
            { 
                return new string[] { 
                    new System.Uri(m_authUri).Host, 
                    string.IsNullOrWhiteSpace(m_simplestorageendpoint) ? null : new System.Uri(m_simplestorageendpoint).Host 
                }; 
            }
        }

        #endregion
        #region IDisposable implementation
        public void Dispose()
        {
        }
        #endregion
    }
}


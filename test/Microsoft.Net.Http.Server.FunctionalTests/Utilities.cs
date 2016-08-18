// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System;

namespace Microsoft.Net.Http.Server
{
    internal static class Utilities
    {
        // When tests projects are run in parallel, overlapping port ranges can cause a race condition when looking for free 
        // ports during dynamic port allocation. To avoid this, make sure the port range here is different from the range in 
        // Microsoft.AspNetCore.Server.WebListener.
        private const int BasePort = 8001;
        private const int MaxPort = 11000;
        private static int NextPort = BasePort;
        private static object PortLock = new object();

        internal static WebListener CreateHttpAuthServer(AuthenticationSchemes authScheme, bool allowAnonymos, out string baseAddress)
        {
            var listener = CreateHttpServer(out baseAddress);
            listener.Settings.Authentication.Schemes = authScheme;
            listener.Settings.Authentication.AllowAnonymous = allowAnonymos;
            return listener;
        }

        internal static WebListener CreateHttpServer(out string baseAddress)
        {
            string root;
            return CreateDynamicHttpServer(string.Empty, out root, out baseAddress);
        }

        internal static WebListener CreateHttpServerReturnRoot(string path, out string root)
        {
            string baseAddress;
            return CreateDynamicHttpServer(path, out root, out baseAddress);
        }

        internal static WebListener CreateDynamicHttpServer(string basePath, out string root, out string baseAddress)
        {
            lock (PortLock)
            {
                while (NextPort < MaxPort)
                {
                    var port = NextPort++;
                    var prefix = UrlPrefix.Create("http", "localhost", port, basePath);
                    root = prefix.Scheme + "://" + prefix.Host + ":" + prefix.Port;
                    baseAddress = prefix.ToString();
                    var listener = new WebListener();
                    listener.Settings.UrlPrefixes.Add(prefix);
                    try
                    {
                        listener.Start();
                        return listener;
                    }
                    catch (WebListenerException)
                    {
                        listener.Dispose();
                    }
                }
                NextPort = BasePort;
            }
            throw new Exception("Failed to locate a free port.");
        }

        internal static WebListener CreateHttpsServer()
        {
            return CreateServer("https", "localhost", 9090, string.Empty);
        }

        internal static WebListener CreateServer(string scheme, string host, int port, string path)
        {
            WebListener listener = new WebListener();
            listener.Settings.UrlPrefixes.Add(UrlPrefix.Create(scheme, host, port, path));
            listener.Start();
            return listener;
        }
    }
}

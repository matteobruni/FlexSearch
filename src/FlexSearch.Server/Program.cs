﻿namespace FlexSearch.Server
{
    using System;
    using System.Collections.Specialized;
    using FlexSearch.Core;
    using Topshelf;
    using Owin;
    using Gibraltar.Agent;
    using System.IO;
    internal class Program
    {
        #region Methods
        private static void Main(string[] args)
        {
            var settings = Core.Main.GetServerSettings(Path.Combine(Constants.ConfFolder, "Config.json"));
            foreach (var file in Directory.EnumerateFiles(Constants.PluginFolder, "*.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    System.Reflection.Assembly.LoadFile(file);
                }
                catch (FileLoadException e) { }
            }

            ILogService logger = Core.Main.GetLoggerService(settings);
            logger.StartSession();

            try
            {

                HostFactory.Run(
                    x =>
                    {
                        x.Service<Main.NodeService>(
                            s =>
                            {
                                s.ConstructUsing(name => new Main.NodeService(settings, false));
                                s.WhenStarted(tc => tc.Start());
                                s.WhenStopped(tc => tc.Stop());
                            });
                        x.RunAsLocalSystem();
                        x.SetDescription("FlexSearch Server");
                        x.SetDisplayName("FlexSearch Server");
                        x.SetServiceName("FlexSearch-Server");
                        x.EnableServiceRecovery(rc => rc.RestartService(1));
                    });
            }
            catch (Exception e)
            {
                logger.TraceCritical(e);
            }

            logger.EndSession();
        }
        #endregion
    }
}
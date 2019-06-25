﻿/*
 * This file is part of the CatLib package.
 *
 * (c) CatLib <support@catlib.io>
 *
 * For the full copyright and license information, please view the LICENSE
 * file that was distributed with this source code.
 *
 * Document: https://catlib.io/
 */

using CatLib.Container;
using CatLib.EventDispatcher;
using CatLib.Support;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using CatLibContainer = CatLib.Container.Container;

namespace CatLib
{
    /// <summary>
    /// The CatLib <see cref="Application"/> instance.
    /// </summary>
    public class Application : CatLibContainer, IApplication
    {
        /// <summary>
        /// The version of the framework application.
        /// </summary>
        private static string version;

        /// <summary>
        /// The types of the loaded service providers.
        /// </summary>
        private readonly List<IServiceProvider> loadedProviders;

        /// <summary>
        /// The main thread id.
        /// </summary>
        private readonly int mainThreadId;

        /// <summary>
        /// True if the application has been bootstrapped.
        /// </summary>
        private bool bootstrapped;

        /// <summary>
        /// True if the application has been initialized.
        /// </summary>
        private bool inited;

        /// <summary>
        /// True if the <see cref="Register"/> is being executed.
        /// </summary>
        private bool registering;

        /// <summary>
        /// The unique runtime id.
        /// </summary>
        private long incrementId;

        /// <summary>
        /// The debug level.
        /// </summary>
        private DebugLevel debugLevel;
        private IEventDispatcher dispatcher;

        /// <summary>
        /// Initializes a new instance of the <see cref="Application"/> class.
        /// </summary>
        /// <param name="global">True if sets the instance to <see cref="App"/> facade.</param>
        public Application(bool global = true)
        {
            loadedProviders = new List<IServiceProvider>();

            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            RegisterBaseBindings();

            // We use closures to save the current context state
            // Do not change to: OnFindType(Type.GetType) This
            // causes the active assembly to be not the expected scope.
            OnFindType(finder => { return Type.GetType(finder); });

            DebugLevel = DebugLevel.Production;
            Process = StartProcess.Construct;

            if (global)
            {
                App.That = this;
            }
        }

        /// <summary>
        /// The framework start process type.
        /// </summary>
        public enum StartProcess
        {
            /// <summary>
            /// When you create a new <see cref="Application"/>,
            /// you are in the <see cref="Construct"/> phase.
            /// </summary>
            Construct = 0,

            /// <summary>
            /// Before the <see cref="Application.Bootstrap"/> call.
            /// </summary>
            Bootstrap = 1,

            /// <summary>
            /// When during <see cref="Application.Bootstrap"/> execution,
            /// you are in the <see cref="Bootstrapping"/> phase.
            /// </summary>
            Bootstrapping = 2,

            /// <summary>
            /// After the <see cref="Application.Bootstrap"/> called.
            /// </summary>
            Bootstraped = 3,

            /// <summary>
            /// Before the <see cref="Application.Init"/> call.
            /// </summary>
            Init = 4,

            /// <summary>
            /// When during <see cref="Application.Init"/> execution,
            /// you are in the <see cref="Initing"/> phase.
            /// </summary>
            Initing = 5,

            /// <summary>
            /// After the <see cref="Application.Init"/> called.
            /// </summary>
            Inited = 6,

            /// <summary>
            /// When the framework running.
            /// </summary>
            Running = 7,

            /// <summary>
            /// Before the <see cref="Application.Terminate"/> call.
            /// </summary>
            Terminate = 8,

            /// <summary>
            /// When during <see cref="Application.Terminate"/> execution,
            /// you are in the <see cref="Terminating"/> phase.
            /// </summary>
            Terminating = 9,

            /// <summary>
            /// After the <see cref="Application.Terminate"/> called.
            /// All resources are destroyed.
            /// </summary>
            Terminated = 10,
        }

        /// <summary>
        /// Gets the CatLib <see cref="Application"/> version.
        /// </summary>
        public static string Version => version ?? (version = FileVersionInfo
                       .GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion);

        /// <summary>
        /// Gets indicates the application startup process.
        /// </summary>
        public StartProcess Process { get; private set; }

        /// <inheritdoc />
        public bool IsMainThread => mainThreadId == Thread.CurrentThread.ManagedThreadId;

        /// <inheritdoc />
        public DebugLevel DebugLevel
        {
            get => debugLevel;
            set
            {
                debugLevel = value;
                Instance(Type2Service(typeof(DebugLevel)), debugLevel);
            }
        }

        /// <inheritdoc cref="Application(bool)"/>
        /// <returns>The CatLib <see cref="Application"/> instance.</returns>
        public static Application New(bool global = true)
        {
            return new Application(global);
        }

        /// <summary>
        /// Sets the event dispatcher.
        /// </summary>
        /// <param name="dispatcher">The event dispatcher instance.</param>
        public void SetDispatcher(IEventDispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            this.Instance<IEventDispatcher>(dispatcher);
        }

        /// <inheritdoc />
        public virtual void Terminate()
        {
            Process = StartProcess.Terminate;
            Dispatch(new BeforeTerminateEventArgs(this));
            Process = StartProcess.Terminating;
            Flush();
            if (App.That == this)
            {
                App.That = null;
            }

            Process = StartProcess.Terminated;
            Dispatch(new AfterTerminateEventArgs(this));
        }

        /// <summary>
        /// Bootstrap the given array of bootstrap classes.
        /// </summary>
        /// <param name="bootstraps">The given bootstrap classes.</param>
        public virtual void Bootstrap(params IBootstrap[] bootstraps)
        {
            Guard.Requires<ArgumentNullException>(bootstraps != null);

            if (bootstrapped || Process != StartProcess.Construct)
            {
                throw new CodeStandardException($"Cannot repeatedly trigger the {nameof(Bootstrap)}()");
            }

            Process = StartProcess.Bootstrap;
            bootstraps = Dispatch(new BeforeBootEventArgs(bootstraps, this))
                            .GetBootstraps();
            Process = StartProcess.Bootstrapping;

            var existed = new HashSet<IBootstrap>();

            foreach (var bootstrap in bootstraps)
            {
                if (bootstrap == null)
                {
                    continue;
                }

                if (existed.Contains(bootstrap))
                {
                    throw new LogicException($"The bootstrap already exists : {bootstrap}");
                }

                existed.Add(bootstrap);

                var skipped = Dispatch(new BootingEventArgs(bootstrap, this))
                                .IsSkip;
                if (!skipped)
                {
                    bootstrap.Bootstrap();
                }
            }

            Process = StartProcess.Bootstraped;
            bootstrapped = true;
            Dispatch(new AfterBootEventArgs(this));
        }

        /// <summary>
        /// Init all of the registered service provider.
        /// </summary>
        public virtual void Init()
        {
            if (!bootstrapped)
            {
                throw new CodeStandardException($"You must call {nameof(Bootstrap)}() first.");
            }

            if (inited || Process != StartProcess.Bootstraped)
            {
                throw new CodeStandardException($"Cannot repeatedly trigger the {nameof(Init)}()");
            }

            Process = StartProcess.Init;
            Dispatch(new BeforeInitEventArgs(this));
            Process = StartProcess.Initing;

            foreach (var provider in loadedProviders)
            {
                InitProvider(provider);
            }

            inited = true;
            Process = StartProcess.Inited;
            Dispatch(new AfterInitEventArgs(this));

            Process = StartProcess.Running;
            Dispatch(new StartCompletedEventArgs(this));
        }

        /// <inheritdoc />
        public virtual void Register(IServiceProvider provider, bool force = false)
        {
            Guard.Requires<ArgumentNullException>(provider != null);

            if (IsRegistered(provider))
            {
                if (!force)
                {
                    throw new LogicException($"Provider [{provider.GetType()}] is already register.");
                }

                loadedProviders.Remove(provider);
            }

            if (Process == StartProcess.Initing)
            {
                throw new CodeStandardException($"Unable to add service provider during {nameof(StartProcess.Initing)}");
            }

            if (Process > StartProcess.Running)
            {
                throw new CodeStandardException($"Unable to {nameof(Terminate)} in-process registration service provider");
            }

            var skipped = Dispatch(new RegisterProviderEventArgs(provider, this))
                            .IsSkip;
            if (skipped)
            {
                return;
            }

            try
            {
                registering = true;
                provider.Register();
            }
            finally
            {
                registering = false;
            }

            loadedProviders.Add(provider);

            if (inited)
            {
                InitProvider(provider);
            }
        }

        /// <inheritdoc />
        public bool IsRegistered(IServiceProvider provider)
        {
            Guard.Requires<ArgumentNullException>(provider != null);
            return loadedProviders.Contains(provider);
        }

        /// <inheritdoc />
        public long GetRuntimeId()
        {
            return Interlocked.Increment(ref incrementId);
        }

        /// <summary>
        /// Call the iterator with the default coroutine.
        /// </summary>
        /// <param name="iterator">The iterator.</param>
        protected static void StartCoroutine(IEnumerator iterator)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(iterator);
            do
            {
                iterator = stack.Pop();
                while (iterator.MoveNext())
                {
                    if (!(iterator.Current is IEnumerator nextCoroutine))
                    {
                        continue;
                    }

                    stack.Push(iterator);
                    iterator = nextCoroutine;
                }
            }
            while (stack.Count > 0);
        }

        /// <summary>
        /// Initialize the specified service provider.
        /// </summary>
        /// <param name="provider">The specified service provider.</param>
        protected virtual void InitProvider(IServiceProvider provider)
        {
            Dispatch(new InitProviderEventArgs(provider, this));
            provider.Init();
        }

        /// <inheritdoc />
        protected override void GuardConstruct(string method)
        {
            if (registering)
            {
                throw new CodeStandardException(
                    $"It is not allowed to make services or dependency injection in the {nameof(Register)} process, method:{method}");
            }

            base.GuardConstruct(method);
        }

        /// <summary>
        /// Register the core service aliases.
        /// </summary>
        private void RegisterBaseBindings()
        {
            this.Singleton<IApplication>(() => this).Alias<Application>().Alias<IContainer>();
            SetDispatcher(new EventDispatcher.EventDispatcher());
        }

        private T Dispatch<T>(T eventArgs)
            where T : EventArgs
        {
            return dispatcher?.Dispatch(eventArgs) ?? eventArgs;
        }
    }
}

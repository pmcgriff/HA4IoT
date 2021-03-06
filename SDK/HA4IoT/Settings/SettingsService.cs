﻿using System;
using System.Collections.Generic;
using HA4IoT.Contracts.Api;
using HA4IoT.Contracts.Services;
using HA4IoT.Contracts.Services.Backup;
using HA4IoT.Contracts.Services.Settings;
using HA4IoT.Contracts.Services.Storage;
using Newtonsoft.Json.Linq;

namespace HA4IoT.Settings
{
    [ApiServiceClass(typeof(ISettingsService))]
    public class SettingsService : ServiceBase, ISettingsService
    {
        private const string StorageFilename = "SettingsService.json";
        private const string BackupKeyName = "Settings";

        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, JObject> _settings = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        private readonly IStorageService _storageService;

        public SettingsService(IBackupService backupService, IStorageService storageService)
        {
            if (backupService == null) throw new ArgumentNullException(nameof(backupService));
            if (storageService == null) throw new ArgumentNullException(nameof(storageService));

            _storageService = storageService;

            backupService.CreatingBackup += (s, e) => CreateBackup(e);
            backupService.RestoringBackup += (s, e) => RestoreBackupNEW(e);
        }

        public void Initialize()
        {
            lock (_syncRoot)
            {
                TryLoadSettings();
            }
        }

        public event EventHandler<SettingsChangedEventArgs> SettingsChanged;

        public void CreateSettingsMonitor<TSettings>(string uri, Action<TSettings> callback)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var initialSettings = GetSettings<TSettings>(uri);
            callback(initialSettings);

            SettingsChanged += (s, e) =>
            {
                if (!e.Uri.Equals(uri))
                {
                    return;
                }

                var updateSettings = GetSettings<TSettings>(uri);
                callback(updateSettings);
            };
        }

        public TSettings GetSettings<TSettings>(string uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            lock (_syncRoot)
            {
                JObject settings;
                if (_settings.TryGetValue(uri, out settings))
                {
                    return settings.ToObject<TSettings>();
                }

                var settingsInstance = Activator.CreateInstance<TSettings>();
                _settings[uri] = JObject.FromObject(settingsInstance);

                SaveSettings();

                return settingsInstance;
            }
        }

        public JObject GetSettings(string uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            lock (_syncRoot)
            {
                JObject settings;
                if (!_settings.TryGetValue(uri, out settings))
                {
                    settings = new JObject();
                }

                return settings;
            }
        }

        public void ImportSettings(string uri, object settings)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            var rawSettings = settings as JObject;
            if (rawSettings == null)
            {
                rawSettings = JObject.FromObject(settings);
            }

            ImportRawSettings(uri, rawSettings);
        }

        [ApiMethod]
        public void Replace(IApiContext apiContext)
        {
            var request = apiContext.Request.ToObject<SettingsServiceApiRequest>();
            SetRawSettings(request.Uri, request.Settings);
        }

        [ApiMethod]
        public void Import(IApiContext apiContext)
        {
            if (apiContext.Request.Type == JTokenType.Object)
            {
                var request = apiContext.Request.ToObject<SettingsServiceApiRequest>();
                ImportRawSettings(request.Uri, request.Settings);
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        [ApiMethod]
        public void ImportMultiple(IApiContext apiContext)
        {
            if (apiContext.Request.Type == JTokenType.Object)
            {
                var request = apiContext.Request.ToObject<Dictionary<string, JObject>>();
                foreach (var item in request)
                {
                    ImportSettings(item.Key, item.Value);
                }
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        [ApiMethod]
        public void GetSettings(IApiContext apiContext)
        {
            if (apiContext.Request.Type == JTokenType.Object)
            {
                var request = apiContext.Request.ToObject<SettingsServiceApiRequest>();
                apiContext.Response = GetSettings(request.Uri);
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private void CreateBackup(BackupEventArgs backupEventArgs)
        {
            lock (_syncRoot)
            {
                backupEventArgs.Backup[BackupKeyName] = JObject.FromObject(_settings);
            }
        }

        private void RestoreBackupNEW(BackupEventArgs backupEventArgs)
        {
            if (backupEventArgs.Backup.Property(BackupKeyName) == null)
            {
                return;
            }

            var settings = backupEventArgs.Backup[BackupKeyName].Value<Dictionary<string, JObject>>();

            lock (_syncRoot)
            {
                foreach (var setting in settings)
                {
                    _settings[setting.Key] = setting.Value;
                }

                SaveSettings();
            }

            foreach (var setting in settings)
            {
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(setting.Key));
            }
        }

        [ApiMethod]
        public void RestoreBackup(IApiContext apiContext)
        {
            if (apiContext.Request.Type == JTokenType.Object)
            {
                var settings = apiContext.Request.ToObject<Dictionary<string, JObject>>();

                lock (_syncRoot)
                {
                    foreach (var setting in settings)
                    {
                        _settings[setting.Key] = setting.Value;
                    }

                    SaveSettings();
                }

                foreach (var setting in settings)
                {
                    SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(setting.Key));
                }
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private void SetRawSettings(string uri, JObject settings)
        {
            lock (_syncRoot)
            {
                _settings[uri] = settings;

                SaveSettings();

                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(uri));
            }
        }

        private void ImportRawSettings(string uri, JObject settings)
        {
            lock (_syncRoot)
            {
                JObject existingSettings;
                if (_settings.TryGetValue(uri, out existingSettings))
                {
                    var mergeSettings = new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace };
                    existingSettings.Merge(settings, mergeSettings);
                }
                else
                {
                    _settings[uri] = settings;
                }

                SaveSettings();
                SettingsChanged?.Invoke(this, new SettingsChangedEventArgs(uri));
            }
        }

        private void SaveSettings()
        {
            _storageService.Write(StorageFilename, _settings);
        }

        private void TryLoadSettings()
        {
            Dictionary<string, JObject> persistedSettings;
            if (_storageService.TryRead(StorageFilename, out persistedSettings))
            {
                foreach (var setting in persistedSettings)
                {
                    _settings[setting.Key] = setting.Value;
                }
            }
        }
    }
}

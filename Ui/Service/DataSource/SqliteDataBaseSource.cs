﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using _1RM.Model.DAO;
using _1RM.Model.DAO.Dapper;
using _1RM.Model.Protocol;
using _1RM.Model.Protocol.Base;
using _1RM.View;
using com.github.xiangyuecn.rsacsharp;
using NUlid;
using NUlid.Rng;
using Shawn.Utils;
using Stylet;

namespace _1RM.Service.DataSource
{
    public partial class SqliteDataSource : IDataService, IDataSource
    {
        private readonly IDataBase _dataBase;

        public SqliteDataSource(string dbFilePath, MonotonicUlidRng? rng = null)
        {
            if (rng == null)
                rng = new NUlid.Rng.MonotonicUlidRng();
            Id = Ulid.NewUlid(rng).ToString();
            DbFilePath = dbFilePath;
            //_dataBase = new DapperDataBase();
            _dataBase = new DapperDataBaseFree();
        }

        public IDataBase DB()
        {
            return _dataBase;
        }

        public bool Database_OpenConnection(DatabaseType type, string newConnectionString)
        {
            // open db connection, or create a sqlite db.
            Debug.Assert(_dataBase != null);
            _dataBase.OpenConnection(DatabaseType.Sqlite, DbExtensions.GetSqliteConnectionString(DbFilePath));
            _dataBase.InitTables();

            // check database rsa encrypt
            var privateKeyPath = _dataBase.GetFromDatabase_RSA_PrivateKeyPath();
            if (!string.IsNullOrWhiteSpace(privateKeyPath)
                && File.Exists(privateKeyPath))
            {
                _rsa = new RSA(File.ReadAllText(Database_GetPrivateKeyPath()), true);
            }
            else
            {
                _rsa = null;
            }
            return true;
        }

        public void Database_CloseConnection()
        {
            Debug.Assert(_dataBase != null);
            if (_dataBase.IsConnected())
                _dataBase.CloseConnection();
        }

        public EnumDbStatus Database_SelfCheck()
        {
            _dataBase?.OpenConnection();
            // check connection
            if (_dataBase?.IsConnected() != true)
                return EnumDbStatus.NotConnected;

            // validate encryption
            var privateKeyPath = _dataBase.GetFromDatabase_RSA_PrivateKeyPath();
            if (string.IsNullOrWhiteSpace(privateKeyPath))
            {
                // no encrypt
                return EnumDbStatus.OK;
            }
            var publicKey = _dataBase.Get_RSA_PublicKey();
            var pks = RSA.CheckPrivatePublicKeyMatch(privateKeyPath, publicKey);
            switch (pks)
            {
                case RSA.EnumRsaStatus.CannotReadPrivateKeyFile:
                    return EnumDbStatus.RsaPrivateKeyNotFound;
                case RSA.EnumRsaStatus.PrivateKeyFormatError:
                    return EnumDbStatus.RsaPrivateKeyFormatError;
                case RSA.EnumRsaStatus.PublicKeyFormatError:
                    return EnumDbStatus.DataIsDamaged;
                case RSA.EnumRsaStatus.PrivateAndPublicMismatch:
                    return EnumDbStatus.RsaNotMatched;
                case RSA.EnumRsaStatus.NoError:
                    break;
            }
            return EnumDbStatus.OK;
        }

        private RSA? _rsa = null;

        public string Database_GetPublicKey()
        {
            Debug.Assert(_dataBase != null);
            return _dataBase?.Get_RSA_PublicKey() ?? "";
        }

        public string Database_GetPrivateKeyPath()
        {
            Debug.Assert(_dataBase != null);
            return _dataBase?.GetFromDatabase_RSA_PrivateKeyPath() ?? "";
        }

        public RSA.EnumRsaStatus Database_SetEncryptionKey(string privateKeyPath, string privateKeyContent, IEnumerable<ProtocolBase> servers)
        {
            Debug.Assert(_dataBase != null);

            // clear rsa key
            if (string.IsNullOrEmpty(privateKeyPath))
            {
                Debug.Assert(_rsa != null);
                Debug.Assert(string.IsNullOrWhiteSpace(Database_GetPrivateKeyPath()) == false);

                // decrypt
                var cloneList = new List<ProtocolBase>();
                foreach (var server in servers)
                {
                    var tmp = (ProtocolBase)server.Clone();
                    tmp.SetNotifyPropertyChangedEnabled(false);
                    DecryptToConnectLevel(ref tmp);
                    cloneList.Add(tmp);
                }

                // update 
                if (_dataBase.SetRsa("", "", cloneList))
                {
                    _rsa = null;
                }
                return RSA.EnumRsaStatus.NoError;
            }
            // set rsa key
            else
            {
                Debug.Assert(_rsa == null);
                Debug.Assert(string.IsNullOrWhiteSpace(Database_GetPrivateKeyPath()) == true);


                var pks = RSA.KeyCheck(privateKeyContent, true);
                if (pks != RSA.EnumRsaStatus.NoError)
                    return pks;
                var rsa = new RSA(privateKeyContent, true);

                // encrypt
                var cloneList = new List<ProtocolBase>();
                foreach (var server in servers)
                {
                    var tmp = (ProtocolBase)server.Clone();
                    tmp.SetNotifyPropertyChangedEnabled(false);
                    EncryptToDatabaseLevel(rsa, ref tmp);
                    cloneList.Add(tmp);
                }

                // update 
                if (_dataBase.SetRsa(privateKeyPath, rsa.ToPEM_PKCS1(true), cloneList))
                {
                    _dataBase.Set_RSA_SHA1(rsa.Sign("SHA1", AppPathHelper.APP_NAME));
                    _rsa = rsa;
                }
                return RSA.EnumRsaStatus.NoError;
            }
        }

        public RSA.EnumRsaStatus Database_UpdatePrivateKeyPathOnly(string privateKeyPath)
        {
            Debug.Assert(_rsa != null);
            Debug.Assert(string.IsNullOrWhiteSpace(Database_GetPrivateKeyPath()) == false);
            Debug.Assert(File.Exists(privateKeyPath));

            var pks = RSA.CheckPrivatePublicKeyMatch(privateKeyPath, Database_GetPublicKey());
            if (pks == RSA.EnumRsaStatus.NoError)
            {
                _dataBase.Set_RSA_PrivateKeyPath(privateKeyPath);
            }
            return pks;
        }

        public string DecryptOrReturnOriginalString(string originalString)
        {
            return DecryptOrReturnOriginalString(_rsa, originalString);
        }

        public static string DecryptOrReturnOriginalString(RSA? ras, string originalString)
        {
            return ras?.DecodeOrNull(originalString) ?? originalString;
        }

        private static string Encrypt(RSA? rsa, string str)
        {
            Debug.Assert(rsa != null);
            if (rsa.DecodeOrNull(str) == null)
                return rsa.Encode(str);
            return str;
        }

        public string Encrypt(string str)
        {
            return Encrypt(_rsa, str);
        }

        public static void EncryptToDatabaseLevel(RSA? rsa, ref ProtocolBase server)
        {
            if (rsa == null) return;
            // ! server must be decrypted
            server.DisplayName = Encrypt(rsa, server.DisplayName);

            // encrypt some info
            if (server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPort)))
            {
                var p = (ProtocolBaseWithAddressPort)server;
                p.Address = Encrypt(rsa, p.Address);
                p.SetPort(Encrypt(rsa, p.Port));
            }
            if (server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPortUserPwd)))
            {
                var p = (ProtocolBaseWithAddressPortUserPwd)server;
                p.UserName = Encrypt(rsa, p.UserName);
            }


            // encrypt password
            if (server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPortUserPwd)))
            {
                var s = (ProtocolBaseWithAddressPortUserPwd)server;
                s.Password = Encrypt(rsa, s.Password);
            }
            switch (server)
            {
                case SSH ssh when !string.IsNullOrWhiteSpace(ssh.PrivateKey):
                    {
                        ssh.PrivateKey = Encrypt(rsa, ssh.PrivateKey);
                        break;
                    }
                case RDP rdp when !string.IsNullOrWhiteSpace(rdp.GatewayPassword):
                    {
                        rdp.GatewayPassword = Encrypt(rsa, rdp.GatewayPassword);
                        break;
                    }
            }
        }
        public void EncryptToDatabaseLevel(ref ProtocolBase server)
        {
            EncryptToDatabaseLevel(_rsa, ref server);
        }

        public void DecryptToRamLevel(ref ProtocolBase server)
        {
            DecryptToRamLevel(_rsa, ref server);
        }

        public static void DecryptToRamLevel(RSA? rsa, ref ProtocolBase server)
        {
            if (rsa == null) return;
            server.DisplayName = DecryptOrReturnOriginalString(rsa, server.DisplayName);
            if (server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPort)))
            {
                var p = (ProtocolBaseWithAddressPort)server;
                p.Address = DecryptOrReturnOriginalString(rsa, p.Address);
                p.Port = DecryptOrReturnOriginalString(rsa, p.Port);
            }

            if (server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPortUserPwd)))
            {
                var p = (ProtocolBaseWithAddressPortUserPwd)server;
                p.UserName = DecryptOrReturnOriginalString(rsa, p.UserName);
            }
        }

        public void DecryptToConnectLevel(ref ProtocolBase server)
        {
            DecryptToConnectLevel(_rsa, ref server);
        }

        public static void DecryptToConnectLevel(RSA? rsa, ref ProtocolBase server)
        {
            if (rsa == null) return;
            DecryptToRamLevel(rsa, ref server);
            if (server.GetType().IsSubclassOf(typeof(ProtocolBaseWithAddressPortUserPwd)))
            {
                var s = (ProtocolBaseWithAddressPortUserPwd)server;
                s.Password = DecryptOrReturnOriginalString(rsa, s.Password);
            }
            switch (server)
            {
                case SSH ssh when !string.IsNullOrWhiteSpace(ssh.PrivateKey):
                    ssh.PrivateKey = DecryptOrReturnOriginalString(rsa, ssh.PrivateKey);
                    break;

                case RDP rdp when !string.IsNullOrWhiteSpace(rdp.GatewayPassword):
                    rdp.GatewayPassword = DecryptOrReturnOriginalString(rsa, rdp.GatewayPassword);
                    break;
            }
        }

        public void Database_InsertServer(ProtocolBase server)
        {
            var tmp = (ProtocolBase)server.Clone();
            tmp.SetNotifyPropertyChangedEnabled(false);
            EncryptToDatabaseLevel(ref tmp);
            _dataBase.AddServer(tmp);
        }

        public void Database_InsertServer(IEnumerable<ProtocolBase> servers)
        {
            var cloneList = new List<ProtocolBase>();
            foreach (var server in servers)
            {
                var tmp = (ProtocolBase)server.Clone();
                tmp.SetNotifyPropertyChangedEnabled(false);
                EncryptToDatabaseLevel(ref tmp);
                cloneList.Add(tmp);
            }
            _dataBase.AddServer(cloneList);
        }

        public bool Database_UpdateServer(ProtocolBase org)
        {
            Debug.Assert(string.IsNullOrEmpty(org.Id) == false);
            var tmp = (ProtocolBase)org.Clone();
            tmp.SetNotifyPropertyChangedEnabled(false);
            EncryptToDatabaseLevel(ref tmp);
            return _dataBase.UpdateServer(tmp);
        }

        public bool Database_UpdateServer(IEnumerable<ProtocolBase> servers)
        {
            var cloneList = new List<ProtocolBase>();
            foreach (var server in servers)
            {
                var tmp = (ProtocolBase)server.Clone();
                tmp.SetNotifyPropertyChangedEnabled(false);
                EncryptToDatabaseLevel(ref tmp);
                cloneList.Add(tmp);
            }
            return _dataBase.UpdateServer(cloneList);
        }

        public bool Database_DeleteServer(string id)
        {
            return IsWritable() && _dataBase.DeleteServer(id);
        }

        public bool Database_DeleteServer(IEnumerable<string> ids)
        {
            return IsWritable() && _dataBase.DeleteServer(ids);
        }

        public List<ProtocolBase> Database_GetServers()
        {
            return _dataBase?.GetServers() ?? new List<ProtocolBase>();
        }
    }





    public partial class SqliteDataSource : IDataSource, IDataService
    {
        public readonly string Id;
        public readonly string DbFilePath;

        public List<ProtocolBaseViewModel> CachedProtocols { get; private set; } = new List<ProtocolBaseViewModel>();
        public string GetDataSourceId()
        {
            throw new NotImplementedException();
        }
        
        public IEnumerable<ProtocolBaseViewModel> GetServers()
        {
            if (NeedReload())
            {
                var protocols = Database_GetServers();
                CachedProtocols = new List<ProtocolBaseViewModel>(protocols.Count);
                foreach (var protocol in protocols)
                {
                    try
                    {
                        var serverAbstract = protocol;
                        Execute.OnUIThread(() =>
                        {
                            this.DecryptToRamLevel(ref serverAbstract);
                            var vm = new ProtocolBaseViewModel(serverAbstract, this)
                            {
                                LastConnectTime = ConnectTimeRecorder.Get(serverAbstract.Id, GetDataSourceId())
                            };
                            CachedProtocols.Add(vm);
                        });
                    }
                    catch (Exception e)
                    {
                        SimpleLogHelper.Info(e);
                    }
                }

                LastUpdateTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
            }

            return CachedProtocols;
        }

        public bool IsReadable()
        {
            return true;
        }

        public bool IsWritable()
        {
            return true;
        }

        public long LastUpdateTimestamp { get; private set; }
        public long DataSourceUpdateTimestamp { get; set; }

        public bool NeedReload()
        {
            return LastUpdateTimestamp < DataSourceUpdateTimestamp;
        }
    }
}
﻿using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Gallifrey.Exceptions.Serialization;
using Newtonsoft.Json;

namespace Gallifrey.Serialization
{
    public class ItemSerializer<T> where T : new()
    {
        private readonly string savePath;
        private string TempWritePath => savePath + ".temp";
        private string BackupPath => savePath + ".bak";

        public ItemSerializer()
        {
        }

        public ItemSerializer(string fileName) : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Gallifrey"), fileName)
        {
        }

        public ItemSerializer(string directory, string fileName)
        {
            try
            {
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                savePath = Path.Combine(directory, fileName);
            }
            catch (Exception)
            {
                savePath = null;
                // this will be evaluated later
            }
        }

        public void Serialize(T obj)
        {
            Serialize(obj, 0);
        }

        private void Serialize(T obj, int retryCount)
        {
            if (savePath == null) throw new SerializerError("No Save Path");

            try
            {
                if (File.Exists(savePath))
                {
                    File.Copy(savePath, TempWritePath, true);
                    if (!File.Exists(BackupPath) || (new FileInfo(BackupPath).LastAccessTime.Date == DateTime.Now.Date))
                    {
                        File.Copy(savePath, BackupPath, true);
                    }
                }
                File.WriteAllText(savePath, DataEncryption.Encrypt(JsonConvert.SerializeObject(obj)));
            }
            catch (Exception ex)
            {
                if (retryCount >= 3)
                {
                    throw new SerializerError("Error in serialization", ex);
                }
                Thread.Sleep(100);
                Serialize(obj, retryCount + 1);
            }
            finally
            {
                try
                {
                    if (File.Exists(TempWritePath))
                    {
                        File.Delete(TempWritePath);
                    }
                }
                catch (Exception)
                {
                    //ignored
                }
            }
        }

        public T DeSerialize()
        {
            if (savePath == null) throw new SerializerError("No Save Path");

            if (!File.Exists(savePath))
            {
                return new T();
            }

            var text = File.ReadAllText(savePath);
            return DeSerialize(text);
        }

        public T DeSerialize(string encryptedString)
        {
            return DeSerialize(encryptedString, 0);
        }

        private T DeSerialize(string encryptedString, int retryCount)
        {
            try
            {
                var text = DataEncryption.Decrypt(encryptedString);
                return JsonConvert.DeserializeObject<T>(text);
            }
            catch (Exception)
            {
                if (retryCount >= 3)
                {
                    return new T();
                }

                Thread.Sleep(100);
                return DeSerialize(encryptedString, retryCount + 1);
            }
        }
    }
}
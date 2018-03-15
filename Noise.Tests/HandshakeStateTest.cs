using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Noise.Tests
{
	public class HandshakeStateTest
	{
		[Fact]
		public void TestCacophony()
		{
			Test("Cacophony.txt");
		}

		[Fact]
		public void TestMultipsk()
		{
			Test("Multipsk.txt");
		}

		private void Test(string file)
		{
			var s = File.ReadAllText(file);
			var json = JObject.Parse(s);

			var initBuffer = new byte[Protocol.MaxMessageLength];
			var respBuffer = new byte[Protocol.MaxMessageLength];

			foreach (var vector in json["vectors"])
			{
				var protocolName = GetString(vector, "protocol_name");

				if (protocolName.Contains("448") || protocolName.Contains("BLAKE2s"))
				{
					continue;
				}

				var initPrologue = GetBytes(vector, "init_prologue");
				var initPsks = GetPsks(vector, "init_psks");
				var initStatic = GetBytes(vector, "init_static");
				var initEphemeral = GetBytes(vector, "init_ephemeral");
				var initRemoteStatic = GetBytes(vector, "init_remote_static");
				var respPrologue = GetBytes(vector, "resp_prologue");
				var respPsks = GetPsks(vector, "resp_psks");
				var respStatic = GetBytes(vector, "resp_static");
				var respEphemeral = GetBytes(vector, "resp_ephemeral");
				var respRemoteStatic = GetBytes(vector, "resp_remote_static");
				var handshakeHash = GetBytes(vector, "handshake_hash");

				var initStaticPair = GetKeyPair(initStatic);
				var respStaticPair = GetKeyPair(respStatic);

				var protocol = Protocol.Parse(protocolName.AsReadOnlySpan());

				var init = protocol.Create(true, initPrologue, initStaticPair, initRemoteStatic, initPsks);
				var resp = protocol.Create(false, respPrologue, respStaticPair, respRemoteStatic, respPsks);

				var flags = BindingFlags.Instance | BindingFlags.NonPublic;
				var setDh = init.GetType().GetMethod("SetDh", flags);

				var initDh = new FixedKeyDh(initEphemeral);
				var respDh = new FixedKeyDh(respEphemeral);

				setDh.Invoke(init, new object[] { initDh });
				setDh.Invoke(resp, new object[] { respDh });

				Transport initTransport = null;
				Transport respTransport = null;

				byte[] initHandshakeHash = null;
				byte[] respHandshakeHash = null;

				foreach (var message in vector["messages"])
				{
					var payload = GetBytes(message, "payload");
					var ciphertext = GetBytes(message, "ciphertext");

					Span<byte> initMessage = null;
					Span<byte> respMessage = null;

					int initMessageSize;
					int respMessageSize;

					if (initTransport == null && respTransport == null)
					{
						(initMessageSize, initHandshakeHash, initTransport) = init.WriteMessage(payload, initBuffer);
						initMessage = initBuffer.AsSpan().Slice(0, initMessageSize);

						(respMessageSize, respHandshakeHash, respTransport) = resp.ReadMessage(initMessage, respBuffer);
						respMessage = respBuffer.AsSpan().Slice(0, respMessageSize);
					}
					else
					{
						initMessageSize = initTransport.WriteMessage(payload, initBuffer);
						initMessage = initBuffer.AsSpan().Slice(0, initMessageSize);

						respMessageSize = respTransport.ReadMessage(initMessage, respBuffer);
						respMessage = respBuffer.AsSpan().Slice(0, respMessageSize);
					}

					Assert.Equal(ciphertext, initMessage.ToArray());
					Assert.Equal(payload, respMessage.ToArray());

					Swap(ref initBuffer, ref respBuffer);
					Swap(ref init, ref resp);

					if (initTransport != null && !initTransport.IsOneWay)
					{
						Swap(ref initTransport, ref respTransport);
					}
				}

				if(handshakeHash.Length > 0)
				{
					Assert.Equal(handshakeHash, initHandshakeHash);
					Assert.Equal(handshakeHash, respHandshakeHash);
				}

				init.Dispose();
				resp.Dispose();

				initTransport?.Dispose();
				respTransport?.Dispose();
			}
		}

		private static string GetString(JToken token, string property)
		{
			return (string)token[property] ?? String.Empty;
		}

		private static byte[] GetBytes(JToken token, string property)
		{
			return Hex.Decode(GetString(token, property));
		}

		private static List<byte[]> GetPsks(JToken token, string property)
		{
			var psks = token[property];

			if (psks == null)
			{
				return null;
			}

			var result = new List<byte[]>();

			foreach (var psk in psks)
			{
				result.Add(Hex.Decode((string)psk));
			}

			return result;
		}

		private static KeyPair GetKeyPair(byte[] privateKey)
		{
			if (privateKey == null || privateKey.Length == 0)
			{
				return null;
			}

			var publicKey = new byte[privateKey.Length];
			Libsodium.crypto_scalarmult_curve25519_base(publicKey, privateKey);

			return new KeyPair(privateKey, publicKey);
		}

		private static void Swap<T>(ref T x, ref T y)
		{
			var temp = x;
			x = y;
			y = temp;
		}
	}
}

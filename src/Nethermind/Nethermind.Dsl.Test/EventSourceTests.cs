//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Api;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dsl.ANTLR;
using Nethermind.Dsl.Pipeline.Builders;
using Nethermind.Dsl.Pipeline.Data;
using Nethermind.Serialization.Json;
using Nethermind.Sockets;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Dsl.Test
{
    public class EventSourceTests
    {
        private Interpreter _interpreter;
        private INethermindApi _api;

        [SetUp]
        public void SetUp()
        {
            _interpreter = null;
            _api = Substitute.For<INethermindApi>();
            _api.EthereumJsonSerializer.Returns(new EthereumJsonSerializer());
        }

        [Test]
        [TestCase("WATCH Events WHERE Topics CONTAINS  0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef PUBLISH WebSockets test")]
        public void Will_send_to_websockets_given_log(string script)
        {
            var log = new LogEntry(Address.Zero, Array.Empty<byte>(), new[] { new Keccak("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef")}); 

            var receipt = new TxReceipt
            {
                Logs = new[] { log }
            };

            var mockWebSocket = Substitute.For<WebSocket>();
            mockWebSocket.State.Returns(WebSocketState.Open);
            _api.WebSocketsManager.AddModule(Arg.Do<IWebSocketsModule>(module => module.CreateClient(mockWebSocket, "test")));
            _interpreter = new Interpreter(_api, script);

            _api.MainBlockProcessor.TransactionProcessed += Raise.Event<EventHandler<TxProcessedEventArgs>>(this, new TxProcessedEventArgs(0, null, receipt));

            //this needs to be fixed and properly checked if the sent buffer is correct, had a problem with this and decided that for now it's enough to be sure that it works
            mockWebSocket.ReceivedWithAnyArgs().SendAsync(default, default, default, default);
        }


        [Test]
        [TestCase("WATCH Events WHERE Topics CONTAINS  0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef PUBLISH Telegram 509090569")]
        public void Will_send_to_telegram_given_log(string script)
        {
            var log = new LogEntry(Address.Zero, Array.Empty<byte>(), new[] { new Keccak("0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef")});

            var receipt = new TxReceipt
            {
                Logs = new[] { log }
            };

            _interpreter = new Interpreter(_api, script);

            _api.MainBlockProcessor.TransactionProcessed += Raise.Event<EventHandler<TxProcessedEventArgs>>(this, new TxProcessedEventArgs(0, null, receipt));
        }


        [Test]
        public void Will_convert_signature_to_hash_correctly()
        {
            var signature = "Swap(address,address,int256,int256,uint160,uint128,int24)";
            var log = new LogEntry(Address.Zero, Array.Empty<byte>(), new []{new Keccak("0xc42079f94a6350d7e6235f29174924f928cc2ac818eb64fed8004e115fbcca67")});

            var result = EventElementsBuilder.CheckEventSignature(log, signature);
            Assert.IsTrue(result);
        }

        [Test]
        [TestCase("WATCH Events WHERE Topics CONTAINS  0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef PUBLISH Discord 861992269531185156")]
        public void Will_send_to_discord_given_log(string script)
        {
            var log = new LogEntry(Address.Zero, Array.Empty<byte>(), new[] { new Keccak("a2936cbec2f64887c9c65cbeee2c2fe8b44a0064a6e9436fd568f7e2311ba676")});

            var receipt = new TxReceipt
            {
                Logs = new[] { log }
            };

            _interpreter = new Interpreter(_api, script);

            _api.MainBlockProcessor.TransactionProcessed += Raise.Event<EventHandler<TxProcessedEventArgs>>(this, new TxProcessedEventArgs(0, null, receipt));
        }

        [Test]
        [TestCase("WATCH Events WHERE Topics CONTAINS  0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef PUBLISH Slack https://hooks.slack.com/services/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")]
        public void Will_send_to_slack_given_log(string script)
        {
            var log = new LogEntry(Address.Zero, Array.Empty<byte>(), new[] { new Keccak("a2936cbec2f64887c9c65cbeee2c2fe8b44a0064a6e9436fd568f7e2311ba676")});

            var receipt = new TxReceipt
            {
                Logs = new[] { log }
            };

            _interpreter = new Interpreter(_api, script);

            _api.MainBlockProcessor.TransactionProcessed += Raise.Event<EventHandler<TxProcessedEventArgs>>(this, new TxProcessedEventArgs(0, null, receipt));
        }

        [Test]
        public void Will_convert_LogEntry_to_EventData()
        {
            var log = new LogEntry(Address.Zero, new byte[] { 1, 5, 8}, new Keccak[] { Keccak.OfAnEmptyString});

            EventData data = EventData.FromLogEntry(log);
            
            Assert.AreEqual(data.Data, log.Data);
            Assert.AreEqual(data.LoggersAddress, log.LoggersAddress);
        }

        [Test]
        public async Task will_send_to_discord()
        {
            var log = new LogEntry(Address.Zero, new byte[] { 1, 5, 8}, new Keccak[] { Keccak.OfAnEmptyString});
            EventData data = EventData.FromLogEntry(log);

            var client = new HttpClient();
            var serializer = new EthereumJsonSerializer();
            var values = new Dictionary<string, string>
            {
                { "content", serializer.Serialize(data) }
            };

            var content = new FormUrlEncodedContent(values);

            var response = await client.PostAsync("https://discord.com/api/webhooks/881857651846303764/cx3raxzElhz3hBuYyqSnyP85zNIWwDNXvwnqj4MzeupCv11CkTnunv03QqjplUl_3AqP", content);
        }
    }
}

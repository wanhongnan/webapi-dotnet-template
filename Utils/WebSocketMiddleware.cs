﻿using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Utils
{
    /// <summary>
    /// [Route("test1/{id}")]
    /// class TestWebSocketMiddleware : WebSocketMiddleware
    /// {
    ///     public TestWebSocketMiddleware(RequestDelegate next) : base(next){}
    ///     protected override Task<string> ProcessMessage(string receiveString)
    ///     {
    ///         return base.ProcessMessage("test1 : " + GetRouteValue("id") + receiveString);
    ///     }
    /// }
    /// 
    /// Microsoft.AspNetCore.WebSockets
    /// 在Startup.cs文件中添加
    ///        app.UseWebSockets();
    ///        app.UseMiddleware<TestWebSocketMiddleware>();
    ///        app.UseMiddleware<TestWebSocketMiddleware2>();
    /// </summary>
    public abstract class WebSocketMiddleware
    {
        private static WebSocketTalbe sockTable = new WebSocketTalbe();

        RequestDelegate _next;
        RouteTemplate _routeTemplate;
        public WebSocketMiddleware(RequestDelegate next)
        {
            _next = next;
            var route = (RouteAttribute)this.GetType().GetCustomAttributes(typeof(RouteAttribute), false).FirstOrDefault();
            _routeTemplate = TemplateParser.Parse(route.Template);
        }

        public async Task Invoke(HttpContext context)
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                await _next.Invoke(context);
                return;
            }

            var defatuls = new RouteValueDictionary(); 
            var matcher = new TemplateMatcher(_routeTemplate, defatuls);
            var ret = matcher.TryMatch(new PathString(context.Request.Path.Value), defatuls);
            if (!ret)
            {
                await _next.Invoke(context);
                return;
            }

            var process = (WebSocketMiddleware)System.Activator.CreateInstance(this.GetType(), _next);
            process._defautls = defatuls;
            await process.ProcessMessage(context);
        }

        RouteValueDictionary _defautls = new RouteValueDictionary();
        /// <summary>
        /// [Route("test1/{id}")]
        /// GetRouteValue("id");
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public string GetRouteValue(string key)
        {
            return (string)_defautls.GetValueOrDefault(key);
        }

        WebSocket _WebSocket = null;
        public WebSocket WebSocket { get { return _WebSocket; } }
        private async Task ProcessMessage(HttpContext context)
        {
            WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
            _WebSocket = webSocket;
            lock (sockTable)
            {
                sockTable.Add(this, webSocket);
            }
            const int maxMessageSize = 10240;
            var receivedDataBuffer = new ArraySegment<Byte>(new Byte[maxMessageSize]);
            var cancellationToken = context.RequestAborted;

            while (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    var webSocketReceiveResult = await webSocket.ReceiveAsync(receivedDataBuffer, cancellationToken);
                    if (webSocketReceiveResult.MessageType == WebSocketMessageType.Close)
                        break;
                    else
                    {
                        byte[] payloadData = receivedDataBuffer.Array.Where(b => b != 0).ToArray();
                        string receiveString = System.Text.Encoding.UTF8.GetString(payloadData, 0, payloadData.Length);
                        var newString = await ProcessMessage(receiveString);
                        Byte[] bytes = System.Text.Encoding.UTF8.GetBytes(newString);
                        await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);

                    }
                }
                catch (Exception) { break; }
            }

            try
            {
                lock (sockTable) { sockTable.Del(this, webSocket); }
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, String.Empty, cancellationToken);
            }
            catch (Exception) { }

        }

        public async Task Close()
        {
            try
            {
                if (_WebSocket != null)
                {
                    var cancellationToken = new CancellationToken();
                    lock (sockTable) { sockTable.Del(this, _WebSocket); }
                    await _WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, String.Empty, cancellationToken);
                }
            }
            catch (Exception) { }
        }

        protected virtual async Task<string> ProcessMessage(string receiveString)
        {
            return await Task.FromResult(receiveString);
        }

        public async static Task SendMessageAll<T>(string msg)
            where T : WebSocketMiddleware
        {
            List<WebSocket> sockets = new List<WebSocket>();
            lock (sockTable)
            {
                sockets = sockTable.GetWebsockets(typeof(T));
            }
            if (sockets.Count == 0) return;

            var cancellationToken = new CancellationToken();
            Byte[] bytes = System.Text.Encoding.UTF8.GetBytes(msg);
            var tasks = sockets
                .Select(f => f.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken));
            await Task.WhenAll(tasks);
        }
    }

    public class WebSocketTalbe
    {
        Dictionary<string, List<WebSocket>> _table = new Dictionary<string, List<WebSocket>>();
        public void Add(WebSocketMiddleware mdw, WebSocket socket)
        {
            var type = mdw.GetType().ToString();
            if (!_table.ContainsKey(type))
                _table.Add(type, new List<WebSocket>());
            var list = _table[type];
            list.Add(socket);
        }

        public void Del(WebSocketMiddleware mdw, WebSocket socket)
        {
            var type = mdw.GetType().ToString();
            if (!_table.ContainsKey(type))
                _table.Add(type, new List<WebSocket>());
            var list = _table[type];
            list.Remove(socket);
        }

        public List<WebSocket> GetWebsockets(Type webSocketMiddlewareType)
        {
            var type = webSocketMiddlewareType.ToString();
            if (!_table.ContainsKey(type))
                _table.Add(type, new List<WebSocket>());
            var list = _table[type];
            return list;
        }
    }    
}


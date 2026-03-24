using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var certPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cloudflared", "cert.pem");
var payload = string.Concat(File.ReadAllLines(certPath).Where(line => !line.Contains("BEGIN") && !line.Contains("END")));
var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
var token = JsonSerializer.Deserialize<LoginToken>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
using var client = new HttpClient();
using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.cloudflare.com/client/v4/zones/{token!.ZoneId}");
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.ApiToken);
var response = await client.SendAsync(request);
Console.WriteLine((int)response.StatusCode);
Console.WriteLine(await response.Content.ReadAsStringAsync());

record LoginToken(string ZoneId, string AccountId, string ApiToken);

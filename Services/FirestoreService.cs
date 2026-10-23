using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PassFlowTotem.Models;

namespace PassFlowTotem.Services
{
    public class FirestoreService
    {
        private const string FirebaseProjectId = "passflow-8907f";
        private const string BaseUrl = $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents";

        private readonly HttpClient _http;

        public FirestoreService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }

        // =========================================================================
        // 1. PAREAMENTO DIRETO COM A COLEÇÃO 'devices' NO FIRESTORE
        // =========================================================================

        public async Task<DeviceConfig?> CheckOrRegisterPinAsync(string pin, DeviceConfig currentConfig)
        {
            try
            {
                var cleanPin = pin.Trim().ToUpper();
                var url = $"{BaseUrl}/devices/{cleanPin}";

                // 1. Tenta buscar o documento existente
                var getResp = await _http.GetAsync(url);
                if (getResp.IsSuccessStatusCode)
                {
                    var json = await getResp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("fields", out var fields))
                    {
                        var status = GetStringField(fields, "status");
                        if (status == "paired")
                        {
                            var orgId = GetStringField(fields, "organizationId");
                            var unitId = GetStringField(fields, "unitId");
                            var allowedServices = GetArrayField(fields, "allowedServices");

                            currentConfig.IsPaired = true;
                            currentConfig.Pin = cleanPin;
                            currentConfig.OrganizationId = orgId;
                            currentConfig.UnitId = string.IsNullOrWhiteSpace(unitId) ? "matriz" : unitId;
                            currentConfig.AllowedServices = allowedServices;

                            return currentConfig;
                        }
                    }
                }
                else if (getResp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // 2. Se não existir, registra o PIN como pendente para aparecer no Admin Web
                    var nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    var payload = new
                    {
                        fields = new Dictionary<string, object>
                        {
                            ["type"] = new { stringValue = "totem" },
                            ["status"] = new { stringValue = "pending" },
                            ["createdAt"] = new { timestampValue = nowIso }
                        }
                    };

                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    await _http.PatchAsync(url, content);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro no Firestore CheckOrRegisterPinAsync: {ex.Message}");
            }

            return null;
        }

        // =========================================================================
        // 2. CARREGAR DADOS DO QUIOSQUE (CATEGORIAS, SERVIÇOS, CUPOM)
        // =========================================================================

        public async Task<(List<Category> categories, List<Service> services, TicketPrintConfig printConfig, string orgName, string unitName)> LoadKioskDataAsync(string orgId, string unitId)
        {
            var categories = new List<Category>();
            var services = new List<Service>();
            var printConfig = new TicketPrintConfig();
            var orgName = "PassFlow Atendimento";
            var unitName = "Unidade Matriz";

            try
            {
                var basePath = $"{BaseUrl}/organizations/{orgId}/units/{unitId}";

                // 1. Carregar Categorias
                try
                {
                    var catResp = await _http.GetAsync($"{basePath}/categories");
                    if (catResp.IsSuccessStatusCode)
                    {
                        var catJson = await catResp.Content.ReadAsStringAsync();
                        categories = ParseCategories(catJson);
                    }
                }
                catch { }

                // 2. Carregar Serviços
                try
                {
                    var srvResp = await _http.GetAsync($"{basePath}/services");
                    if (srvResp.IsSuccessStatusCode)
                    {
                        var srvJson = await srvResp.Content.ReadAsStringAsync();
                        services = ParseServices(srvJson);
                    }
                }
                catch { }

                // 3. Carregar Modelo do Cupom (ticketPrintConfig)
                try
                {
                    var cfgResp = await _http.GetAsync($"{basePath}/settings/ticketPrintConfig");
                    if (cfgResp.IsSuccessStatusCode)
                    {
                        var cfgJson = await cfgResp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(cfgJson);
                        if (doc.RootElement.TryGetProperty("fields", out var fields))
                        {
                            var rawVal = GetStringField(fields, "value");
                            if (!string.IsNullOrWhiteSpace(rawVal))
                            {
                                var parsed = JsonSerializer.Deserialize<TicketPrintConfig>(rawVal);
                                if (parsed != null) printConfig = parsed;
                            }
                        }
                    }
                }
                catch { }

                // 4. Carregar Nome da Organização
                try
                {
                    var orgResp = await _http.GetAsync($"{basePath}/settings/organizationName");
                    if (orgResp.IsSuccessStatusCode)
                    {
                        var orgJson = await orgResp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(orgJson);
                        if (doc.RootElement.TryGetProperty("fields", out var fields))
                        {
                            var name = GetStringField(fields, "value");
                            if (!string.IsNullOrWhiteSpace(name)) orgName = name;
                        }
                    }
                }
                catch { }

                // 5. Carregar Nome da Unidade
                try
                {
                    var unitDocResp = await _http.GetAsync($"{BaseUrl}/organizations/{orgId}/units/{unitId}");
                    if (unitDocResp.IsSuccessStatusCode)
                    {
                        var uJson = await unitDocResp.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(uJson);
                        if (doc.RootElement.TryGetProperty("fields", out var fields))
                        {
                            var name = GetStringField(fields, "name");
                            if (!string.IsNullOrWhiteSpace(name)) unitName = name;
                        }
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao carregar dados do quiosque no Firestore: {ex.Message}");
            }

            return (categories, services, printConfig, orgName, unitName);
        }

        // =========================================================================
        // 3. EMITIR E SALVAR SENHA NO FIRESTORE
        // =========================================================================

        public async Task<GeneratedTicket> CreateTicketAsync(
            string serverUrl,
            string orgId,
            string unitId,
            Service service,
            string typeCode,
            string typeName,
            SubService? subService)
        {
            var isPriority = typeCode.Contains("pref", StringComparison.OrdinalIgnoreCase);
            var prefix = isPriority ? "P" : "A";

            // Calcula o próximo número sequencial da senha consultando a coleção de tickets
            int nextSeq = 1;
            int queuePosition = 1;

            try
            {
                var ticketsUrl = $"{BaseUrl}/organizations/{orgId}/units/{unitId}/tickets";
                var tResp = await _http.GetAsync(ticketsUrl);
                if (tResp.IsSuccessStatusCode)
                {
                    var tJson = await tResp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(tJson);
                    if (doc.RootElement.TryGetProperty("documents", out var docs) && docs.ValueKind == JsonValueKind.Array)
                    {
                        int maxNum = 0;
                        int waitingCount = 0;
                        foreach (var d in docs.EnumerateArray())
                        {
                            if (d.TryGetProperty("fields", out var f))
                            {
                                var numStr = GetStringField(f, "number");
                                var status = GetStringField(f, "status");
                                var sId = GetStringField(f, "serviceId");

                                if (status == "waiting" && sId == service.Id)
                                {
                                    waitingCount++;
                                }

                                if (numStr.StartsWith($"{prefix}-", StringComparison.OrdinalIgnoreCase))
                                {
                                    var parts = numStr.Split('-');
                                    if (parts.Length > 1 && int.TryParse(parts[1], out var parsedNum))
                                    {
                                        if (parsedNum > maxNum) maxNum = parsedNum;
                                    }
                                }
                            }
                        }

                        nextSeq = maxNum + 1;
                        queuePosition = Math.Max(1, waitingCount + 1);
                    }
                }
            }
            catch { }

            var ticketNumber = $"{prefix}-{nextSeq:D3}";
            var nowUtc = DateTime.UtcNow;
            var nowLocal = DateTime.Now;

            // Grava o novo ticket no Firestore
            try
            {
                var postUrl = $"{BaseUrl}/organizations/{orgId}/units/{unitId}/tickets";
                var fields = new Dictionary<string, object>
                {
                    ["number"] = new { stringValue = ticketNumber },
                    ["serviceId"] = new { stringValue = service.Id },
                    ["serviceName"] = new { stringValue = service.Name },
                    ["ticketTypeName"] = new { stringValue = typeName },
                    ["status"] = new { stringValue = "waiting" },
                    ["priorityWeight"] = new { integerValue = isPriority ? "2" : "1" },
                    ["timestamp"] = new { timestampValue = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                    ["createdTimestamp"] = new { timestampValue = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
                };

                if (!string.IsNullOrWhiteSpace(service.CategoryId))
                {
                    fields["serviceCategoryId"] = new { stringValue = service.CategoryId };
                }

                if (subService != null)
                {
                    fields["subServiceId"] = new { stringValue = subService.Id };
                    fields["subServiceName"] = new { stringValue = subService.Name };
                }

                var content = new StringContent(JsonSerializer.Serialize(new { fields }), Encoding.UTF8, "application/json");
                await _http.PostAsync(postUrl, content);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao gravar ticket no Firestore: {ex.Message}");
            }

            var cleanServer = serverUrl.TrimEnd('/');
            var virtualQueueUrl = $"{cleanServer}/my-ticket?ticket={Uri.EscapeDataString(ticketNumber)}&orgId={Uri.EscapeDataString(orgId)}&unitId={Uri.EscapeDataString(unitId)}";

            return new GeneratedTicket
            {
                Id = Guid.NewGuid().ToString(),
                Number = ticketNumber,
                ServiceId = service.Id,
                ServiceName = service.Name,
                SubServiceName = subService?.Name,
                TicketTypeId = isPriority ? "type-pref" : "type-normal",
                TicketTypeName = typeName,
                CreatedAt = nowLocal,
                QueuePosition = queuePosition,
                VirtualQueueUrl = virtualQueueUrl
            };
        }

        // =========================================================================
        // AUXILIARES DE PARSER DO FIRESTORE
        // =========================================================================

        private List<Category> ParseCategories(string json)
        {
            var list = new List<Category>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("documents", out var docs) && docs.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in docs.EnumerateArray())
                {
                    var id = d.GetProperty("name").GetString()?.Split('/').Last() ?? "";
                    if (d.TryGetProperty("fields", out var f))
                    {
                        var name = GetStringField(f, "name");
                        var icon = GetStringField(f, "icon");
                        list.Add(new Category { Id = id, Name = name, Icon = icon });
                    }
                }
            }
            return list;
        }

        private List<Service> ParseServices(string json)
        {
            var list = new List<Service>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("documents", out var docs) && docs.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in docs.EnumerateArray())
                {
                    var id = d.GetProperty("name").GetString()?.Split('/').Last() ?? "";
                    if (d.TryGetProperty("fields", out var f))
                    {
                        var name = GetStringField(f, "name");
                        var catId = GetStringField(f, "categoryId");
                        var icon = GetStringField(f, "icon");
                        list.Add(new Service { Id = id, Name = name, CategoryId = catId, Icon = icon });
                    }
                }
            }
            return list;
        }

        private string GetStringField(JsonElement fields, string key)
        {
            if (fields.TryGetProperty(key, out var prop) && prop.TryGetProperty("stringValue", out var val))
            {
                return val.GetString() ?? "";
            }
            return "";
        }

        private List<string> GetArrayField(JsonElement fields, string key)
        {
            var result = new List<string>();
            if (fields.TryGetProperty(key, out var prop) && prop.TryGetProperty("arrayValue", out var arr))
            {
                if (arr.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in vals.EnumerateArray())
                    {
                        if (v.TryGetProperty("stringValue", out var s))
                        {
                            var str = s.GetString();
                            if (!string.IsNullOrWhiteSpace(str)) result.Add(str);
                        }
                    }
                }
            }
            return result;
        }
    }
}

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
        // 2. CARREGAR DADOS DO QUIOSQUE (CATEGORIAS, SERVIÇOS, TIPOS DE SENHA, CUPOM)
        // =========================================================================

        public async Task<(List<Category> categories, List<Service> services, List<TicketType> ticketTypes, TicketPrintConfig printConfig, string orgName, string unitName)> LoadKioskDataAsync(string orgId, string unitId)
        {
            var categories = new List<Category>();
            var services = new List<Service>();
            var ticketTypes = new List<TicketType>();
            var printConfig = new TicketPrintConfig();
            var orgName = "PassFlow Atendimento";
            var unitName = "Unidade Matriz";

            try
            {
                var unitPath = $"{BaseUrl}/organizations/{orgId}/units/{unitId}";
                var sharedPath = $"{BaseUrl}/organizations/{orgId}/units/shared";

                // 1. Carregar Categorias (Unit-specific + Shared)
                var rawCats = new List<Category>();
                rawCats.AddRange(await FetchCollectionAsync<Category>($"{unitPath}/categories", (id, f) => ParseCategory(id, f, unitId)));
                if (unitId != "shared")
                {
                    rawCats.AddRange(await FetchCollectionAsync<Category>($"{sharedPath}/categories", (id, f) => ParseCategory(id, f, unitId)));
                }
                categories = rawCats.GroupBy(c => c.Id).Select(g => g.First()).OrderBy(c => c.Name).ToList();

                // 2. Carregar Serviços (Unit-specific + Shared)
                var rawServices = new List<Service>();
                rawServices.AddRange(await FetchCollectionAsync<Service>($"{unitPath}/services", (id, f) => ParseService(id, f, unitId)));
                if (unitId != "shared")
                {
                    rawServices.AddRange(await FetchCollectionAsync<Service>($"{sharedPath}/services", (id, f) => ParseService(id, f, unitId)));
                }
                services = rawServices.GroupBy(s => s.Id).Select(g => g.First()).OrderBy(s => s.Name).ToList();

                // 3. Carregar Tipos de Senha (ticket_types) (Unit-specific + Shared)
                var rawTypes = new List<TicketType>();
                rawTypes.AddRange(await FetchCollectionAsync<TicketType>($"{unitPath}/ticket_types", (id, f) => ParseTicketType(id, f, unitId)));
                if (unitId != "shared")
                {
                    rawTypes.AddRange(await FetchCollectionAsync<TicketType>($"{sharedPath}/ticket_types", (id, f) => ParseTicketType(id, f, unitId)));
                }
                ticketTypes = rawTypes.GroupBy(t => t.Id).Select(g => g.First()).OrderByDescending(t => t.PriorityWeight).ToList();

                // Se não houver tipos cadastrados, adiciona os padrões oficiais do PassFlow
                if (ticketTypes.Count == 0)
                {
                    ticketTypes.Add(new TicketType { Id = "type-normal", Name = "Atendimento Convencional", Prefix = "A", PriorityWeight = 1, Icon = "User", Description = "Atendimento padrão por ordem de chegada" });
                    ticketTypes.Add(new TicketType { Id = "type-pref", Name = "Atendimento Prioritário", Prefix = "P", PriorityWeight = 5, Icon = "Heart", Description = "Lei 10.048/2000: Idosos (60+), PCD, Gestantes, Lactantes e TEA" });
                }

                // 4. Carregar Modelo do Cupom (ticketPrintConfig)
                printConfig = await LoadSettingAsync<TicketPrintConfig>($"{unitPath}/settings/ticketPrintConfig")
                              ?? await LoadSettingAsync<TicketPrintConfig>($"{sharedPath}/settings/ticketPrintConfig")
                              ?? new TicketPrintConfig();

                // 5. Carregar Nome da Organização
                var customOrgName = await LoadStringSettingAsync($"{unitPath}/settings/organizationName")
                                    ?? await LoadStringSettingAsync($"{sharedPath}/settings/organizationName");
                if (!string.IsNullOrWhiteSpace(customOrgName)) orgName = customOrgName;

                // 6. Carregar Nome da Unidade
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

            return (categories, services, ticketTypes, printConfig, orgName, unitName);
        }

        // =========================================================================
        // 3. EMITIR E SALVAR SENHA NO FIRESTORE COM PREFIXO DINÂMICO DO TIPO
        // =========================================================================

        public async Task<GeneratedTicket> CreateTicketAsync(
            string serverUrl,
            string orgId,
            string unitId,
            Service service,
            TicketType ticketType,
            SubService? subService)
        {
            var prefix = !string.IsNullOrWhiteSpace(ticketType.Prefix) ? ticketType.Prefix.ToUpper() : "A";
            int priorityWeight = ticketType.PriorityWeight > 0 ? ticketType.PriorityWeight : 1;

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
                    ["ticketTypeId"] = new { stringValue = ticketType.Id },
                    ["ticketTypeName"] = new { stringValue = ticketType.Name },
                    ["status"] = new { stringValue = "waiting" },
                    ["priorityWeight"] = new { integerValue = priorityWeight.ToString() },
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
                TicketTypeId = ticketType.Id,
                TicketTypeName = ticketType.Name,
                CreatedAt = nowLocal,
                QueuePosition = queuePosition,
                VirtualQueueUrl = virtualQueueUrl
            };
        }

        // =========================================================================
        // PARSERS E AUXILIARES
        // =========================================================================

        private async Task<List<T>> FetchCollectionAsync<T>(string url, Func<string, JsonElement, T?> parser) where T : class
        {
            var list = new List<T>();
            try
            {
                var resp = await _http.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("documents", out var docs) && docs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var d in docs.EnumerateArray())
                        {
                            var id = d.GetProperty("name").GetString()?.Split('/').Last() ?? "";
                            if (d.TryGetProperty("fields", out var f))
                            {
                                var item = parser(id, f);
                                if (item != null) list.Add(item);
                            }
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        private Category? ParseCategory(string id, JsonElement f, string unitId)
        {
            if (!IsItemAllowedForUnit(f, unitId)) return null;
            return new Category
            {
                Id = id,
                Name = GetStringField(f, "name"),
                Icon = GetStringField(f, "icon")
            };
        }

        private Service? ParseService(string id, JsonElement f, string unitId)
        {
            if (!IsItemAllowedForUnit(f, unitId)) return null;

            var srv = new Service
            {
                Id = id,
                Name = GetStringField(f, "name"),
                CategoryId = GetStringField(f, "categoryId"),
                Icon = GetStringField(f, "icon"),
                SubServices = new List<SubService>()
            };

            // Parse subservices se houver
            if (f.TryGetProperty("subServices", out var subProp) && subProp.TryGetProperty("arrayValue", out var arr))
            {
                if (arr.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in vals.EnumerateArray())
                    {
                        if (v.TryGetProperty("mapValue", out var map) && map.TryGetProperty("fields", out var mf))
                        {
                            srv.SubServices.Add(new SubService
                            {
                                Id = GetStringField(mf, "id"),
                                Name = GetStringField(mf, "name"),
                                Description = GetStringField(mf, "description")
                            });
                        }
                    }
                }
            }

            return srv;
        }

        private TicketType? ParseTicketType(string id, JsonElement f, string unitId)
        {
            if (!IsItemAllowedForUnit(f, unitId)) return null;

            int.TryParse(GetIntegerField(f, "priorityWeight"), out var pw);
            if (pw == 0) pw = 1;

            return new TicketType
            {
                Id = id,
                Name = GetStringField(f, "name"),
                Description = GetStringField(f, "description"),
                Prefix = GetStringField(f, "prefix"),
                PriorityWeight = pw,
                Icon = GetStringField(f, "icon")
            };
        }

        private bool IsItemAllowedForUnit(JsonElement f, string unitId)
        {
            if (unitId == "shared") return true;

            var isAllUnits = GetBooleanField(f, "isAllUnits");
            if (isAllUnits) return true;

            var targetUnits = GetArrayField(f, "targetUnitIds");
            if (targetUnits.Count == 0) return true; // Se não especificado, permite

            return targetUnits.Contains(unitId);
        }

        private async Task<T?> LoadSettingAsync<T>(string url) where T : class
        {
            try
            {
                var resp = await _http.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("fields", out var f))
                    {
                        var rawVal = GetStringField(f, "value");
                        if (!string.IsNullOrWhiteSpace(rawVal))
                        {
                            return JsonSerializer.Deserialize<T>(rawVal);
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private async Task<string?> LoadStringSettingAsync(string url)
        {
            try
            {
                var resp = await _http.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("fields", out var f))
                    {
                        return GetStringField(f, "value");
                    }
                }
            }
            catch { }
            return null;
        }

        private string GetStringField(JsonElement fields, string key)
        {
            if (fields.TryGetProperty(key, out var prop) && prop.TryGetProperty("stringValue", out var val))
            {
                return val.GetString() ?? "";
            }
            return "";
        }

        private string GetIntegerField(JsonElement fields, string key)
        {
            if (fields.TryGetProperty(key, out var prop) && prop.TryGetProperty("integerValue", out var val))
            {
                return val.GetString() ?? "0";
            }
            return "0";
        }

        private bool GetBooleanField(JsonElement fields, string key)
        {
            if (fields.TryGetProperty(key, out var prop) && prop.TryGetProperty("booleanValue", out var val))
            {
                return val.GetBoolean();
            }
            return false;
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

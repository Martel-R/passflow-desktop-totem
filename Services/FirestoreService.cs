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
        // 2. CARREGAR DADOS DO QUIOSQUE (CATEGORIAS, SERVIÇOS, TIPOS DE SENHA, BRANDING, CUPOM)
        // =========================================================================

        public async Task<(List<Category> categories, List<Service> services, List<TicketType> ticketTypes, TicketPrintConfig printConfig, BrandingInfo branding, string unitName)> LoadKioskDataAsync(string orgId, string unitId)
        {
            var categories = new List<Category>();
            var services = new List<Service>();
            var ticketTypes = new List<TicketType>();
            var printConfig = new TicketPrintConfig();
            var branding = new BrandingInfo();
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

                if (ticketTypes.Count == 0)
                {
                    ticketTypes.Add(new TicketType { Id = "type-normal", Name = "Atendimento Convencional", Prefix = "A", PriorityWeight = 1, Icon = "User", Description = "Atendimento padrão por ordem de chegada" });
                    ticketTypes.Add(new TicketType { Id = "type-pref", Name = "Atendimento Prioritário", Prefix = "P", PriorityWeight = 5, Icon = "Heart", Description = "Lei 10.048/2000: Idosos (60+), PCD, Gestantes, Lactantes e TEA" });
                }

                // 4. Carregar Modelo do Cupom (ticketPrintConfig) respeitando herança (Unit -> Shared -> Padrão)
                printConfig = await LoadSettingAsync<TicketPrintConfig>($"{unitPath}/settings/ticketPrintConfig");
                if (printConfig == null || string.IsNullOrWhiteSpace(printConfig.PaperWidth))
                {
                    printConfig = await LoadSettingAsync<TicketPrintConfig>($"{sharedPath}/settings/ticketPrintConfig") ?? new TicketPrintConfig();
                }

                // 5. Carregar Branding (Identidade Visual): Nome, Logotipo, Cores Primária, Destaque e Fundo
                var useCustomBranding = (await LoadStringSettingAsync($"{unitPath}/settings/use_custom_branding")) == "true";
                var brandingSourcePath = useCustomBranding ? unitPath : sharedPath;

                var orgNameVal = await LoadStringSettingAsync($"{brandingSourcePath}/settings/organizationName");
                if (string.IsNullOrWhiteSpace(orgNameVal) && !useCustomBranding)
                {
                    orgNameVal = await LoadStringSettingAsync($"{unitPath}/settings/organizationName");
                }
                if (!string.IsNullOrWhiteSpace(orgNameVal)) branding.OrganizationName = orgNameVal;

                var logoVal = await LoadStringSettingAsync($"{brandingSourcePath}/settings/organizationLogo");
                if (string.IsNullOrWhiteSpace(logoVal) && !useCustomBranding)
                {
                    logoVal = await LoadStringSettingAsync($"{unitPath}/settings/organizationLogo");
                }
                if (!string.IsNullOrWhiteSpace(logoVal)) branding.OrganizationLogo = logoVal;

                var primaryVal = await LoadStringSettingAsync($"{brandingSourcePath}/settings/theme.primary");
                if (!string.IsNullOrWhiteSpace(primaryVal)) branding.PrimaryColor = HslOrCssToHex(primaryVal, "#2563EB");

                var accentVal = await LoadStringSettingAsync($"{brandingSourcePath}/settings/theme.accent");
                if (!string.IsNullOrWhiteSpace(accentVal)) branding.AccentColor = HslOrCssToHex(accentVal, "#10B981");

                var bgVal = await LoadStringSettingAsync($"{brandingSourcePath}/settings/theme.background");
                if (!string.IsNullOrWhiteSpace(bgVal)) branding.BackgroundColor = HslOrCssToHex(bgVal, "#0F172A");

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

            return (categories, services, ticketTypes, printConfig ?? new TicketPrintConfig(), branding, unitName);
        }

        // =========================================================================
        // 3. EMITIR E SALVAR SENHA NO FIRESTORE
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
            if (targetUnits.Count == 0) return true;

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

        // =========================================================================
        // CONVERSÃO DE CORES: HSL (Passflow/Tailwind) -> HEX (#RRGGBB)
        // =========================================================================

        public static string HslOrCssToHex(string? input, string fallbackHex)
        {
            if (string.IsNullOrWhiteSpace(input)) return fallbackHex;
            var trimmed = input.Trim();

            if (trimmed.StartsWith("#")) return trimmed;

            // Formato PassFlow: "231 45% 26%" ou "hsl(231, 45%, 26%)"
            var clean = trimmed.Replace("hsl(", "").Replace(")", "").Replace(",", " ");
            var parts = clean.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                if (double.TryParse(parts[0], out var h) &&
                    double.TryParse(parts[1].Replace("%", ""), out var s) &&
                    double.TryParse(parts[2].Replace("%", ""), out var l))
                {
                    s /= 100.0;
                    l /= 100.0;

                    double r, g, b;
                    if (s == 0)
                    {
                        r = g = b = l;
                    }
                    else
                    {
                        double q = l < 0.5 ? l * (1.0 + s) : l + s - (l * s);
                        double p = (2.0 * l) - q;
                        r = HueToRgb(p, q, (h / 360.0) + (1.0 / 3.0));
                        g = HueToRgb(p, q, h / 360.0);
                        b = HueToRgb(p, q, (h / 360.0) - (1.0 / 3.0));
                    }

                    int ri = (int)Math.Clamp(Math.Round(r * 255.0), 0, 255);
                    int gi = (int)Math.Clamp(Math.Round(g * 255.0), 0, 255);
                    int bi = (int)Math.Clamp(Math.Round(b * 255.0), 0, 255);

                    return $"#{ri:X2}{gi:X2}{bi:X2}";
                }
            }

            return fallbackHex;
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0.0) t += 1.0;
            if (t > 1.0) t -= 1.0;
            if (t < 1.0 / 6.0) return p + ((q - p) * 6.0 * t);
            if (t < 1.0 / 2.0) return q;
            if (t < 2.0 / 3.0) return p + ((q - p) * ((2.0 / 3.0) - t) * 6.0);
            return p;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PassFlowTotem.Models
{
    public class DeviceConfig
    {
        public string ServerUrl { get; set; } = "https://passflow.raevan.com.br";
        public string Pin { get; set; } = "";
        public bool IsPaired { get; set; } = false;
        public string OrganizationId { get; set; } = "";
        public string OrganizationName { get; set; } = "PassFlow";
        public string OrganizationLogo { get; set; } = "";
        public string PrimaryColor { get; set; } = "#2563EB";
        public string AccentColor { get; set; } = "#10B981";
        public string BackgroundColor { get; set; } = "#0F172A";
        public string UnitId { get; set; } = "";
        public string UnitName { get; set; } = "Unidade Principal";
        public List<string> AllowedServices { get; set; } = new();
        public string PrinterName { get; set; } = ""; // Vazio = Impressora Padrão do Windows
    }

    public class BrandingInfo
    {
        public string OrganizationName { get; set; } = "PassFlow Atendimento";
        public string OrganizationLogo { get; set; } = "";
        public string PrimaryColor { get; set; } = "#2563EB";
        public string AccentColor { get; set; } = "#10B981";
        public string BackgroundColor { get; set; } = "#0F172A";
    }

    public class Category
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = "";
    }

    public class SubService
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
    }

    public class Service
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("categoryId")]
        public string CategoryId { get; set; } = "";

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = "";

        [JsonPropertyName("subServices")]
        public List<SubService>? SubServices { get; set; } = new();
    }

    public class TicketType
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [JsonPropertyName("prefix")]
        public string Prefix { get; set; } = "A";

        [JsonPropertyName("priorityWeight")]
        public int PriorityWeight { get; set; } = 1;

        [JsonPropertyName("icon")]
        public string Icon { get; set; } = "";
    }

    public class TicketPrintConfig
    {
        [JsonPropertyName("template")]
        public string Template { get; set; } = "classic"; // classic, compact, modern, minimal

        [JsonPropertyName("paperWidth")]
        public string PaperWidth { get; set; } = "80mm"; // 80mm ou 58mm

        [JsonPropertyName("ticketNumberScale")]
        public string TicketNumberScale { get; set; } = "lg"; // md, lg, xl

        [JsonPropertyName("dividerStyle")]
        public string DividerStyle { get; set; } = "dashed"; // dashed, solid, none

        [JsonPropertyName("showOuterBorder")]
        public bool ShowOuterBorder { get; set; } = false;

        [JsonPropertyName("showLogo")]
        public bool ShowLogo { get; set; } = false;

        [JsonPropertyName("showOrganizationName")]
        public bool ShowOrganizationName { get; set; } = true;

        [JsonPropertyName("customHeaderTitle")]
        public string CustomHeaderTitle { get; set; } = "";

        [JsonPropertyName("headerSubtitle")]
        public string HeaderSubtitle { get; set; } = "";

        [JsonPropertyName("showUnitName")]
        public bool ShowUnitName { get; set; } = true;

        [JsonPropertyName("welcomeMessage")]
        public string WelcomeMessage { get; set; } = "";

        [JsonPropertyName("ticketLabel")]
        public string TicketLabel { get; set; } = "Sua Senha:";

        [JsonPropertyName("showCategoryName")]
        public bool ShowCategoryName { get; set; } = false;

        [JsonPropertyName("showServiceName")]
        public bool ShowServiceName { get; set; } = true;

        [JsonPropertyName("showSubServiceName")]
        public bool ShowSubServiceName { get; set; } = true;

        [JsonPropertyName("showTicketTypeName")]
        public bool ShowTicketTypeName { get; set; } = true;

        [JsonPropertyName("showDateTime")]
        public bool ShowDateTime { get; set; } = true;

        [JsonPropertyName("showQueuePosition")]
        public bool ShowQueuePosition { get; set; } = false;

        [JsonPropertyName("showQrCode")]
        public bool ShowQrCode { get; set; } = true;

        [JsonPropertyName("qrCodeSize")]
        public string QrCodeSize { get; set; } = "md"; // sm, md, lg

        [JsonPropertyName("qrCodeTitle")]
        public string QrCodeTitle { get; set; } = "ACOMPANHE A FILA PELO CELULAR";

        [JsonPropertyName("qrCodeSubtitle")]
        public string QrCodeSubtitle { get; set; } = "Aponte a câmera do celular para o QR Code";

        [JsonPropertyName("footerMessage")]
        public string FooterMessage { get; set; } = "Aguarde e será chamado.";

        [JsonPropertyName("extraFooterNote")]
        public string ExtraFooterNote { get; set; } = "";
    }

    public class GeneratedTicket
    {
        public string Id { get; set; } = "";
        public string Number { get; set; } = "";
        public string ServiceId { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public string? SubServiceName { get; set; }
        public string TicketTypeId { get; set; } = "";
        public string TicketTypeName { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public int QueuePosition { get; set; } = 1;
        public string VirtualQueueUrl { get; set; } = "";
    }
}

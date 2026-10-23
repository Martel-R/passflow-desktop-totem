using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;
using PassFlowTotem.Models;
using QRCoder;

namespace PassFlowTotem.Services
{
    public class ThermalPrinterService
    {
        public void PrintTicket(GeneratedTicket ticket, TicketPrintConfig config, DeviceConfig device)
        {
            try
            {
                var doc = new PrintDocument();

                // Define a impressora específica ou a Padrão do Windows
                if (!string.IsNullOrWhiteSpace(device.PrinterName))
                {
                    doc.PrinterSettings.PrinterName = device.PrinterName;
                }

                // Controlador padrão sem caixa de diálogo (impressão 100% silenciosa)
                doc.PrintController = new StandardPrintController();

                // Determina largura da bobina (80mm = ~280px a 203 DPI; 58mm = ~200px a 203 DPI)
                int pageWidth = config.PaperWidth == "58mm" ? 200 : 285;
                doc.DefaultPageSettings.PaperSize = new PaperSize("CustomThermal", pageWidth, 1200);
                doc.DefaultPageSettings.Margins = new Margins(10, 10, 10, 20);

                doc.PrintPage += (sender, e) =>
                {
                    RenderTicket(e.Graphics, ticket, config, device, pageWidth);
                    e.HasMorePages = false;
                };

                doc.Print();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao imprimir cupom na impressora padrão: {ex.Message}");
                throw;
            }
        }

        private void RenderTicket(Graphics? g, GeneratedTicket ticket, TicketPrintConfig config, DeviceConfig device, int width)
        {
            if (g == null) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float y = 10;
            var centerFormat = new StringFormat { Alignment = StringAlignment.Center };
            var leftFormat = new StringFormat { Alignment = StringAlignment.Near };

            var fontOrg = new Font("Arial", 11, FontStyle.Bold);
            var fontHeader = new Font("Arial", 9, FontStyle.Regular);
            var fontUnit = new Font("Arial", 8, FontStyle.Italic);
            var fontService = new Font("Arial", 12, FontStyle.Bold);
            var fontSubService = new Font("Arial", 10, FontStyle.Regular);
            var fontSmall = new Font("Arial", 8, FontStyle.Regular);
            var fontFooter = new Font("Arial", 8, FontStyle.Regular);

            // Escala da Senha (md: 24pt, lg: 32pt, xl: 40pt)
            float ticketFontSize = config.TicketNumberScale switch
            {
                "xl" => 40f,
                "md" => 24f,
                _ => 32f
            };
            var fontTicket = new Font("Arial", ticketFontSize, FontStyle.Bold);

            var brushBlack = Brushes.Black;
            var penSolid = new Pen(Color.Black, 1.2f);
            var penDashed = new Pen(Color.Black, 1f) { DashStyle = DashStyle.Dash };

            void DrawDivider()
            {
                if (config.DividerStyle == "none") return;
                var pen = config.DividerStyle == "solid" ? penSolid : penDashed;
                g.DrawLine(pen, 5, y, width - 5, y);
                y += 8;
            }

            // 1. CABEÇALHO & IDENTIDADE
            if (config.ShowOrganizationName)
            {
                var orgName = !string.IsNullOrWhiteSpace(config.CustomHeaderTitle)
                    ? config.CustomHeaderTitle
                    : device.OrganizationName;

                g.DrawString(orgName, fontOrg, brushBlack, new RectangleF(0, y, width, 24), centerFormat);
                y += 20;
            }

            if (!string.IsNullOrWhiteSpace(config.HeaderSubtitle))
            {
                g.DrawString(config.HeaderSubtitle, fontHeader, brushBlack, new RectangleF(0, y, width, 18), centerFormat);
                y += 16;
            }

            if (config.ShowUnitName && !string.IsNullOrWhiteSpace(device.UnitName))
            {
                g.DrawString(device.UnitName, fontUnit, brushBlack, new RectangleF(0, y, width, 16), centerFormat);
                y += 16;
            }

            if (!string.IsNullOrWhiteSpace(config.WelcomeMessage))
            {
                g.DrawString(config.WelcomeMessage, fontSmall, brushBlack, new RectangleF(0, y, width, 20), centerFormat);
                y += 18;
            }

            y += 4;
            DrawDivider();

            // 2. TÍTULO / LABEL DA SENHA
            if (!string.IsNullOrWhiteSpace(config.TicketLabel))
            {
                g.DrawString(config.TicketLabel, fontSmall, brushBlack, new RectangleF(0, y, width, 16), centerFormat);
                y += 15;
            }

            // 3. NÚMERO DA SENHA EM DESTAQUE
            float numHeight = ticketFontSize + 16;
            g.DrawString(ticket.Number, fontTicket, brushBlack, new RectangleF(0, y, width, numHeight), centerFormat);
            y += numHeight + 4;

            // 4. INFORMAÇÕES DO SERVIÇO & TIPO DE ATENDIMENTO
            if (config.ShowServiceName && !string.IsNullOrWhiteSpace(ticket.ServiceName))
            {
                g.DrawString(ticket.ServiceName, fontService, brushBlack, new RectangleF(0, y, width, 22), centerFormat);
                y += 20;
            }

            if (config.ShowSubServiceName && !string.IsNullOrWhiteSpace(ticket.SubServiceName))
            {
                g.DrawString(ticket.SubServiceName, fontSubService, brushBlack, new RectangleF(0, y, width, 18), centerFormat);
                y += 18;
            }

            if (config.ShowTicketTypeName && !string.IsNullOrWhiteSpace(ticket.TicketTypeName))
            {
                var fontType = new Font("Arial", 9, FontStyle.Bold);
                g.DrawString($"[{ticket.TicketTypeName.ToUpper()}]", fontType, brushBlack, new RectangleF(0, y, width, 18), centerFormat);
                y += 18;
            }

            y += 4;
            DrawDivider();

            // 5. DATA, HORA & POSIÇÃO NA FILA
            if (config.ShowDateTime)
            {
                var dtStr = ticket.CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
                g.DrawString($"Data/Hora: {dtStr}", fontSmall, brushBlack, new RectangleF(10, y, width - 20, 16), leftFormat);
                y += 15;
            }

            if (config.ShowQueuePosition)
            {
                g.DrawString($"Pessoas na sua frente: {ticket.QueuePosition}", fontSmall, brushBlack, new RectangleF(10, y, width - 20, 16), leftFormat);
                y += 16;
            }

            // 6. QR CODE DA FILA VIRTUAL
            if (config.ShowQrCode && !string.IsNullOrWhiteSpace(ticket.VirtualQueueUrl))
            {
                y += 6;
                DrawDivider();

                if (!string.IsNullOrWhiteSpace(config.QrCodeTitle))
                {
                    var fontQrTitle = new Font("Arial", 8, FontStyle.Bold);
                    g.DrawString(config.QrCodeTitle, fontQrTitle, brushBlack, new RectangleF(0, y, width, 16), centerFormat);
                    y += 15;
                }

                if (!string.IsNullOrWhiteSpace(config.QrCodeSubtitle))
                {
                    g.DrawString(config.QrCodeSubtitle, fontSmall, brushBlack, new RectangleF(0, y, width, 16), centerFormat);
                    y += 15;
                }

                // Gera QR Code via QRCoder
                int qrPixelSize = config.QrCodeSize switch
                {
                    "lg" => 130,
                    "sm" => 80,
                    _ => 105
                };

                using var qrGenerator = new QRCodeGenerator();
                using var qrData = qrGenerator.CreateQrCode(ticket.VirtualQueueUrl, QRCodeGenerator.ECCLevel.M);
                using var qrCode = new QRCode(qrData);
                using var qrBitmap = qrCode.GetGraphic(4);

                float qrX = (width - qrPixelSize) / 2f;
                g.DrawImage(qrBitmap, qrX, y, qrPixelSize, qrPixelSize);
                y += qrPixelSize + 8;
            }

            // 7. MENSAGENS DE RODAPÉ
            if (!string.IsNullOrWhiteSpace(config.FooterMessage))
            {
                y += 4;
                DrawDivider();
                g.DrawString(config.FooterMessage, fontFooter, brushBlack, new RectangleF(0, y, width, 18), centerFormat);
                y += 16;
            }

            if (!string.IsNullOrWhiteSpace(config.ExtraFooterNote))
            {
                g.DrawString(config.ExtraFooterNote, fontSmall, brushBlack, new RectangleF(0, y, width, 20), centerFormat);
                y += 18;
            }

            // Espaço final para guilhotina / corte manual
            y += 25;
            g.DrawString(".", fontSmall, Brushes.White, new PointF(0, y));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PassFlowTotem.Models;
using PassFlowTotem.Services;

namespace PassFlowTotem
{
    public partial class MainWindow : Window
    {
        private readonly ConfigService _configService;
        private readonly FirestoreService _firestoreService;
        private readonly ThermalPrinterService _printerService;

        private DeviceConfig _device;
        private TicketPrintConfig _printConfig = new();
        private List<Category> _categories = new();
        private List<Service> _services = new();
        private List<TicketType> _ticketTypes = new();

        private Category? _selectedCategory;
        private Service? _selectedService;
        private SubService? _selectedSubService;

        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _pairingTimer;
        private readonly DispatcherTimer _inactivityTimer;

        private int _logoClickCount = 0;
        private DateTime _lastLogoClick = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();

            _configService = new ConfigService();
            _firestoreService = new FirestoreService();
            _printerService = new ThermalPrinterService();

            _device = _configService.Load();

            // Relógio em tempo real
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) => TxtClock.Text = DateTime.Now.ToString("HH:mm:ss");
            _clockTimer.Start();

            // Timer de Pareamento (Pooling a cada 2.5 segundos)
            _pairingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _pairingTimer.Tick += async (s, e) => await CheckPairingStatusAsync();

            // Timer de Inatividade (Retorna ao início após 30 segundos sem toque)
            _inactivityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _inactivityTimer.Tick += (s, e) =>
            {
                _inactivityTimer.Stop();
                ResetKioskToHome();
            };
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (_device.IsPaired && !string.IsNullOrWhiteSpace(_device.OrganizationId) && !string.IsNullOrWhiteSpace(_device.UnitId))
            {
                await StartKioskModeAsync();
            }
            else
            {
                await StartPairingModeAsync();
            }
        }

        // =========================================================================
        // FLUXO 1: PAREAMENTO DO TOTEM
        // =========================================================================

        private async Task StartPairingModeAsync()
        {
            PairingPanel.Visibility = Visibility.Visible;
            KioskPanel.Visibility = Visibility.Collapsed;
            SuccessPanel.Visibility = Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(_device.Pin) || _device.Pin.Length != 6)
            {
                _device.Pin = GenerateNewPin();
                _configService.Save(_device);
            }

            TxtPairingPin.Text = _device.Pin;
            TxtPairingStatus.Text = "Aguardando confirmação do painel central...";

            // Registra o PIN na coleção 'devices' do Firestore para aparecer no Admin
            await _firestoreService.CheckOrRegisterPinAsync(_device.Pin, _device);
            _pairingTimer.Start();
        }

        private async Task CheckPairingStatusAsync()
        {
            var paired = await _firestoreService.CheckOrRegisterPinAsync(_device.Pin, _device);
            if (paired != null && paired.IsPaired)
            {
                _pairingTimer.Stop();
                _device = paired;
                _configService.Save(_device);

                TxtPairingStatus.Text = "✓ Pareamento confirmado pelo servidor!";
                await Task.Delay(1000);

                await StartKioskModeAsync();
            }
        }

        private string GenerateNewPin()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, 6).Select(s => s[random.Next(s.Length)]).ToArray());
        }

        private async void BtnRefreshPin_Click(object sender, RoutedEventArgs e)
        {
            _device.Pin = GenerateNewPin();
            _configService.Save(_device);
            TxtPairingPin.Text = _device.Pin;
            await _firestoreService.CheckOrRegisterPinAsync(_device.Pin, _device);
        }

        // =========================================================================
        // FLUXO 2: MODO QUIOSQUE / AUTOATENDIMENTO
        // =========================================================================

        private async Task StartKioskModeAsync()
        {
            _pairingTimer.Stop();
            PairingPanel.Visibility = Visibility.Collapsed;
            SuccessPanel.Visibility = Visibility.Collapsed;
            KioskPanel.Visibility = Visibility.Visible;

            TxtOrgName.Text = _device.OrganizationName;
            TxtUnitName.Text = _device.UnitName;

            await LoadServerDataAsync();
            ResetKioskToHome();
        }

        private async Task LoadServerDataAsync()
        {
            var (categories, services, ticketTypes, printConfig, orgName, unitName) =
                await _firestoreService.LoadKioskDataAsync(_device.OrganizationId, _device.UnitId);

            _categories = categories;
            _services = services;
            _ticketTypes = ticketTypes;
            _printConfig = printConfig;
            _device.OrganizationName = orgName;
            _device.UnitName = unitName;

            TxtOrgName.Text = orgName;
            TxtUnitName.Text = unitName;

            _configService.Save(_device);
        }

        private void ResetKioskToHome()
        {
            _selectedCategory = null;
            _selectedService = null;
            _selectedSubService = null;

            BtnBack.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Collapsed;

            // Se existirem categorias cadastradas, exibe primeiro as categorias;
            // caso contrário, exibe diretamente a lista de serviços.
            if (_categories.Count > 0)
            {
                ShowCategoriesStep();
            }
            else
            {
                ShowServicesStep();
            }
        }

        private void RestartInactivityTimer()
        {
            _inactivityTimer.Stop();
            _inactivityTimer.Start();
        }

        // PASSO 1: CATEGORIAS (DINÂMICAS)
        private void ShowCategoriesStep()
        {
            RestartInactivityTimer();

            TxtStepTitle.Text = "Passo 1: Selecione a Categoria de Atendimento";
            PanelCategories.Visibility = Visibility.Visible;
            PanelServices.Visibility = Visibility.Collapsed;
            PanelSubServices.Visibility = Visibility.Collapsed;
            PanelTicketTypes.Visibility = Visibility.Collapsed;

            BtnBack.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Collapsed;

            PanelCategories.Children.Clear();

            foreach (var cat in _categories)
            {
                // Conta quantos serviços pertencem a esta categoria
                var srvCount = _services.Count(s => s.CategoryId == cat.Id);
                var desc = srvCount > 0 ? $"{srvCount} serviço(s) disponível(is)" : "Toque para ver os serviços";

                var btn = CreateTouchCard(cat.Name, desc, cat.Icon, () =>
                {
                    _selectedCategory = cat;
                    ShowServicesStep();
                });
                PanelCategories.Children.Add(btn);
            }
        }

        // PASSO 2: SERVIÇOS (DINÂMICOS)
        private void ShowServicesStep()
        {
            RestartInactivityTimer();

            var catLabel = _selectedCategory != null ? $" ({_selectedCategory.Name})" : "";
            TxtStepTitle.Text = $"Passo 2: Selecione o Serviço{catLabel}";

            PanelCategories.Visibility = Visibility.Collapsed;
            PanelServices.Visibility = Visibility.Visible;
            PanelSubServices.Visibility = Visibility.Collapsed;
            PanelTicketTypes.Visibility = Visibility.Collapsed;

            BtnBack.Visibility = _categories.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Visible;

            PanelServices.Children.Clear();

            // Filtra serviços pela categoria selecionada (se houver) e pelos serviços permitidos no pareamento do totem
            var availableServices = _services
                .Where(s => _selectedCategory == null || s.CategoryId == _selectedCategory.Id || string.IsNullOrWhiteSpace(s.CategoryId))
                .Where(s => _device.AllowedServices == null || _device.AllowedServices.Count == 0 || _device.AllowedServices.Contains(s.Id))
                .ToList();

            if (availableServices.Count == 0)
            {
                availableServices.Add(new Service { Id = "srv-1", Name = "Atendimento Geral" });
            }

            foreach (var srv in availableServices)
            {
                var desc = srv.SubServices != null && srv.SubServices.Count > 0
                    ? "Possui opções adicionais"
                    : "Toque para emitir senha";

                var btn = CreateTouchCard(srv.Name, desc, srv.Icon, () =>
                {
                    _selectedService = srv;
                    if (srv.SubServices != null && srv.SubServices.Count > 0)
                    {
                        ShowSubServicesStep();
                    }
                    else
                    {
                        ShowTicketTypesStep();
                    }
                });
                PanelServices.Children.Add(btn);
            }
        }

        // PASSO 3: SUBSERVIÇOS (DINÂMICOS)
        private void ShowSubServicesStep()
        {
            RestartInactivityTimer();

            TxtStepTitle.Text = $"Passo 3: Especifique o Atendimento ({_selectedService?.Name})";
            PanelCategories.Visibility = Visibility.Collapsed;
            PanelServices.Visibility = Visibility.Collapsed;
            PanelSubServices.Visibility = Visibility.Visible;
            PanelTicketTypes.Visibility = Visibility.Collapsed;

            BtnBack.Visibility = Visibility.Visible;
            BtnCancel.Visibility = Visibility.Visible;

            PanelSubServices.Children.Clear();

            var subServices = _selectedService?.SubServices ?? new List<SubService>();
            foreach (var sub in subServices)
            {
                var btn = CreateTouchCard(sub.Name, sub.Description ?? "Opção de atendimento", "", () =>
                {
                    _selectedSubService = sub;
                    ShowTicketTypesStep();
                });
                PanelSubServices.Children.Add(btn);
            }
        }

        // PASSO 4: TIPOS DE SENHA (DINÂMICOS CONFORME CONFIGURADO NO PASSFLOW)
        private void ShowTicketTypesStep()
        {
            RestartInactivityTimer();

            TxtStepTitle.Text = "Passo 4: Tipo de Atendimento";
            PanelCategories.Visibility = Visibility.Collapsed;
            PanelServices.Visibility = Visibility.Collapsed;
            PanelSubServices.Visibility = Visibility.Collapsed;
            PanelTicketTypes.Visibility = Visibility.Visible;

            BtnBack.Visibility = Visibility.Visible;
            BtnCancel.Visibility = Visibility.Visible;

            PanelTicketTypesWrap.Children.Clear();

            foreach (var tt in _ticketTypes)
            {
                var isPriority = tt.PriorityWeight > 1 || tt.Prefix.Equals("P", StringComparison.OrdinalIgnoreCase);
                var borderBrush = isPriority ? new SolidColorBrush(Color.FromRgb(245, 158, 11)) : new SolidColorBrush(Color.FromRgb(59, 130, 246));
                var desc = !string.IsNullOrWhiteSpace(tt.Description)
                    ? tt.Description
                    : (isPriority ? "Atendimento prioritário legal" : "Atendimento convencional por ordem de chegada");

                var btn = CreateTouchCard(tt.Name, desc, tt.Icon, async () =>
                {
                    await GenerateAndPrintTicketAsync(tt);
                }, borderBrush, tt.Prefix);

                PanelTicketTypesWrap.Children.Add(btn);
            }
        }

        private async Task GenerateAndPrintTicketAsync(TicketType ticketType)
        {
            _inactivityTimer.Stop();

            var service = _selectedService ?? new Service { Id = "srv-1", Name = "Atendimento Geral" };

            // Emite e grava o ticket diretamente no Firestore do PassFlow com o prefixo e peso do tipo configurado
            var ticket = await _firestoreService.CreateTicketAsync(
                _device.ServerUrl,
                _device.OrganizationId,
                _device.UnitId,
                service,
                ticketType,
                _selectedSubService
            );

            // 1. Exibe tela de sucesso
            TxtSuccessTicketNumber.Text = ticket.Number;
            TxtSuccessService.Text = ticket.ServiceName;
            TxtSuccessType.Text = $"[{ticketType.Name.ToUpper()}]";

            KioskPanel.Visibility = Visibility.Collapsed;
            SuccessPanel.Visibility = Visibility.Visible;

            // 2. Dispara a impressão silenciosa na impressora padrão do Windows
            _ = Task.Run(() =>
            {
                try
                {
                    _printerService.PrintTicket(ticket, _printConfig, _device);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erro na impressão: {ex.Message}");
                }
            });

            // 3. Retorna automaticamente para o início após 5 segundos
            await Task.Delay(5000);
            SuccessPanel.Visibility = Visibility.Collapsed;
            ResetKioskToHome();
            KioskPanel.Visibility = Visibility.Visible;
        }

        private Button CreateTouchCard(string title, string description, string icon, Action onClick, Brush? customBorder = null, string? badgePrefix = null)
        {
            var btn = new Button
            {
                Style = (Style)FindResource("TouchCardButton"),
                Margin = new Thickness(10)
            };

            if (customBorder != null)
            {
                btn.BorderBrush = customBorder;
            }

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var emoji = GetIconEmoji(icon);
            var txtEmoji = new TextBlock
            {
                Text = emoji,
                FontSize = 38,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var txtTitle = new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            var txtDesc = new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };

            stack.Children.Add(txtEmoji);
            stack.Children.Add(txtTitle);
            stack.Children.Add(txtDesc);

            if (!string.IsNullOrWhiteSpace(badgePrefix))
            {
                var txtBadge = new TextBlock
                {
                    Text = $"Prefixo: {badgePrefix.ToUpper()}",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(96, 165, 250)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                stack.Children.Add(txtBadge);
            }

            btn.Content = stack;
            btn.Click += (s, e) => onClick();

            return btn;
        }

        private string GetIconEmoji(string? icon)
        {
            if (string.IsNullOrWhiteSpace(icon)) return "📋";
            return icon.ToLower() switch
            {
                "users" or "user" => "👥",
                "dollarsign" or "dollar" or "finance" => "💰",
                "zap" or "fast" or "speed" => "⚡",
                "stethoscope" or "health" or "squareactivity" or "smile" or "heart" or "activity" => "🩺",
                "filetext" or "document" => "📄",
                "creditcard" or "card" => "💳",
                "shoppingbag" or "store" => "🛍️",
                "wrench" or "tool" => "🔧",
                "alertcircle" or "alert" => "🚨",
                _ => "📋"
            };
        }

        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            if (PanelTicketTypes.Visibility == Visibility.Visible)
            {
                if (_selectedService?.SubServices != null && _selectedService.SubServices.Count > 0)
                    ShowSubServicesStep();
                else
                    ShowServicesStep();
            }
            else if (PanelSubServices.Visibility == Visibility.Visible)
            {
                ShowServicesStep();
            }
            else if (PanelServices.Visibility == Visibility.Visible)
            {
                if (_categories.Count > 0)
                    ShowCategoriesStep();
                else
                    ResetKioskToHome();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            ResetKioskToHome();
        }

        // =========================================================================
        // ATALHO & MODAL DE CONFIGURAÇÃO / ADMIN
        // =========================================================================

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F12 || ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0 && e.Key == Key.S))
            {
                OpenAdminModal();
            }
            else if (e.Key == Key.Escape)
            {
                if (AdminModal.Visibility == Visibility.Visible)
                {
                    AdminModal.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void Logo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var now = DateTime.Now;
            if ((now - _lastLogoClick).TotalSeconds < 2)
            {
                _logoClickCount++;
                if (_logoClickCount >= 5)
                {
                    _logoClickCount = 0;
                    OpenAdminModal();
                }
            }
            else
            {
                _logoClickCount = 1;
            }
            _lastLogoClick = now;
        }

        private void BtnServerConfig_Click(object sender, RoutedEventArgs e)
        {
            OpenAdminModal();
        }

        private void OpenAdminModal()
        {
            TxtServerInput.Text = _device.ServerUrl;

            CmbPrinters.Items.Clear();
            CmbPrinters.Items.Add("🖨️ (Impressora Padrão do Windows)");

            int selectedIndex = 0;
            int i = 1;
            foreach (string printer in PrinterSettings.InstalledPrinters)
            {
                CmbPrinters.Items.Add(printer);
                if (!string.IsNullOrWhiteSpace(_device.PrinterName) && printer.Equals(_device.PrinterName, StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = i;
                }
                i++;
            }
            CmbPrinters.SelectedIndex = selectedIndex;

            AdminModal.Visibility = Visibility.Visible;
        }

        private void BtnSaveAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(TxtServerInput.Text))
            {
                _device.ServerUrl = TxtServerInput.Text.Trim();
            }

            if (CmbPrinters.SelectedIndex > 0)
            {
                _device.PrinterName = CmbPrinters.SelectedItem.ToString() ?? "";
            }
            else
            {
                _device.PrinterName = "";
            }

            _configService.Save(_device);
            AdminModal.Visibility = Visibility.Collapsed;

            MessageBox.Show("Configurações salvas com sucesso!", "PassFlow Totem", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnCloseAdmin_Click(object sender, RoutedEventArgs e)
        {
            AdminModal.Visibility = Visibility.Collapsed;
        }

        private void BtnTestPrint_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var fakeTicket = new GeneratedTicket
                {
                    Id = "test-1",
                    Number = "TST-099",
                    ServiceName = "Teste de Impressão Térmica",
                    TicketTypeName = "Convencional",
                    CreatedAt = DateTime.Now,
                    QueuePosition = 1,
                    VirtualQueueUrl = $"{_device.ServerUrl}/my-ticket?ticket=TST-099"
                };

                var testDevice = new DeviceConfig
                {
                    OrganizationName = _device.OrganizationName,
                    UnitName = _device.UnitName,
                    PrinterName = CmbPrinters.SelectedIndex > 0 ? CmbPrinters.SelectedItem.ToString() ?? "" : ""
                };

                _printerService.PrintTicket(fakeTicket, _printConfig, testDevice);
                MessageBox.Show("Comando de teste de impressão enviado com sucesso à impressora!", "Impressão", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Falha ao imprimir na impressora selecionada:\n\n{ex.Message}", "Erro de Impressão", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnUnpair_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Tem certeza que deseja desvincular este Totem?\n\nEle voltará para a tela inicial de pareamento com código PIN.",
                "Desvincular Totem",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (confirm == MessageBoxResult.Yes)
            {
                _device.IsPaired = false;
                _device.Pin = "";
                _device.OrganizationId = "";
                _device.UnitId = "";
                _configService.Save(_device);

                AdminModal.Visibility = Visibility.Collapsed;
                await StartPairingModeAsync();
            }
        }
    }
}

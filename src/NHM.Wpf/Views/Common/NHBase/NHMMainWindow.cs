﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NHM.Wpf.Views.Common.NHBase
{
    public abstract class NHMMainWindow : BaseDialogWindow
    {

        Grid _gridLayoutRootOverlay;
        Grid _gridLayoutRootOverlay_MODAL_WINDOW_ContentGrid;
        Grid _gridLayoutRootOverlay_MODAL_WINDOW;
        ContentPresenter _contentPresenter;
        // the template style MUST mirror the enum names!!!
        protected enum ToggleButtonType
        {
            DashboardButton,
            DevicesButton,
            BenchmarkButton,
            PluginsButton,
            SettingsButton,
            HelpButton,
        };

        private ToggleButtonType? _lastSelected;

        protected Dictionary<ToggleButtonType, ToggleButton> Tabs { get; private set; } = new Dictionary<ToggleButtonType, ToggleButton>();

        private bool HideInitTabButtonVisibility(string name)
        {
            if ("MinimizeButton" == name) return false;
            if ("CloseButton" == name) return false;
            return true;
        }

        protected abstract void OnTabSelected(ToggleButtonType tabType);

        public override void OnApplyTemplate()
        {
            foreach (var key in Enum.GetValues(typeof(ToggleButtonType)).Cast<ToggleButtonType>())
            {
                var name = key.ToString();
                var tabButtom = GetRequiredTemplateChild<ToggleButton>(name);
                if (tabButtom == null) throw new Exception($"Template Missing ToggleButton with name '{name}'. Make sure your Sytle template contains ToggleButton with name '{name}'.");
                tabButtom.Click += TabButtonButton_Click;
                tabButtom.IsEnabled = false;
                if (HideInitTabButtonVisibility(name))
                {
                    tabButtom.Visibility = Visibility.Hidden;
                }
                Tabs[key] = tabButtom;
            }
            _gridLayoutRootOverlay = GetRequiredTemplateChild<Grid>("LayoutRootOverlay");
            _gridLayoutRootOverlay_MODAL_WINDOW = GetRequiredTemplateChild<Grid>("MODAL_WINDOW_BLUR");
            _gridLayoutRootOverlay_MODAL_WINDOW_ContentGrid = GetRequiredTemplateChild<Grid>("MODAL_WINDOW_ContentGrid");
            
            _gridLayoutRootOverlay_MODAL_WINDOW_ContentGrid.MouseDown += _gridLayoutRootOverlay_MouseDown;
            _gridLayoutRootOverlay_MODAL_WINDOW.MouseDown += _gridLayoutRootOverlay_MouseDown;
            _gridLayoutRootOverlay.MouseDown += _gridLayoutRootOverlay_MouseDown;

            _contentPresenter = GetRequiredTemplateChild<ContentPresenter>("MODAL_DIALOG");
            if (_contentPresenter != null)
            {
                _contentPresenter.AddHandler(Grid.MouseLeftButtonDownEvent, new MouseButtonEventHandler(this.OnHeaderBarMouseLeftButtonDown));
            }
            base.OnApplyTemplate();
        }

        private void _gridLayoutRootOverlay_MouseDown(object sender, MouseEventArgs e)
        {
            _gridLayoutRootOverlay.Visibility = Visibility.Hidden;
        }

        protected void SetTabButtonsEnabled()
        {
            foreach (var kvp in Tabs)
            {
                kvp.Value.IsEnabled = true;
                kvp.Value.Visibility = Visibility.Visible;
            }
            const ToggleButtonType initTab = ToggleButtonType.DashboardButton;
            _lastSelected = initTab;
            Tabs[initTab].IsChecked = true;
            OnTabSelected(initTab);
        }

        private void TabButtonButton_Click(object sender, RoutedEventArgs e)
        {
            // sender must be of ToggleButton Type
            var tabButton = (ToggleButton)sender;

            var currentKey = (ToggleButtonType)Enum.Parse(typeof(ToggleButtonType), tabButton.Name);
            if (_lastSelected == currentKey)
            {
                tabButton.IsChecked = true;
                return;
            }
            else
            {
                // select new 
                _lastSelected = currentKey;
                Tabs[currentKey].IsChecked = true;
                OnTabSelected(currentKey);
                // deselect other
                var deselectKeys = Tabs.Keys.Where(key => key != currentKey);
                foreach (var key in deselectKeys) Tabs[key].IsChecked = false;
            }
        }

        public void ShowContentAsModal(UserControl userControl)
        {
            _contentPresenter.Content = userControl;
            _gridLayoutRootOverlay.Visibility = Visibility.Visible;
        }

        public void HideModal()
        {
            _gridLayoutRootOverlay.Visibility = Visibility.Hidden;
        }

    }
}

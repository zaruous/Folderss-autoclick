using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Folderss.Plugins;

namespace AutoClick
{
    /// <summary>
    /// 플러그인 진입점. plugin.json의 "type"(네임스페이스.클래스)과 이름이 같아야 한다.
    /// public이고 인수 없는 public 생성자가 있어야 한다.
    /// </summary>
    public sealed class AutoClickPlugin : IFolderssPlugin
    {
        private IPluginManager _manager;
        private AutoClickView _openView;

        /// <summary>처음 실행할 때 한 번 호출된다.</summary>
        public void Initialize(IPluginManager manager)
        {
            _manager = manager;
        }

        /// <summary>
        /// ⋯ 메뉴 > 플러그인에서 실행할 때마다 호출된다. 돌려준 요소가 팝업 창의 내용이 된다.
        /// 전역 단축키가 창마다 겹치지 않도록 AutoClick 창은 하나만 연다. 이미 열려 있으면 그 창을 앞으로 가져오고,
        /// 본체가 새로 띄운 창은 뜨자마자 닫는다(CreateView는 null을 돌려줄 수 없다).
        /// </summary>
        public FrameworkElement CreateView()
        {
            if (_openView != null && _openView.IsOpen)
            {
                _openView.BringToFront();
                return ClosingNotice();
            }

            var view = new AutoClickView(_manager);
            view.Closed += (s, e) =>
            {
                if (_openView == view)
                    _openView = null;
            };
            _openView = view;
            return view;
        }

        private FrameworkElement ClosingNotice()
        {
            var notice = new TextBlock { Text = "AutoClick 창이 이미 열려 있습니다.", Margin = new Thickness(16) };
            notice.Loaded += (s, e) =>
            {
                // 본체의 Show()가 끝난 뒤에 닫는다
                notice.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var window = Window.GetWindow(notice);
                    if (window != null)
                        window.Close();
                    if (_openView != null)
                        _openView.BringToFront();
                }), DispatcherPriority.Background);
            };
            return notice;
        }
    }
}

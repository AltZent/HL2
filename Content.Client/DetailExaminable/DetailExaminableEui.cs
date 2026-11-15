using Content.Client.Eui;
using Content.Shared.DetailExaminable;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Client.DetailExaminable
{
    [UsedImplicitly]
    public sealed class DetailExaminableEui : BaseEui
    {
        private readonly DetailExaminableEditWindow _window;

        public DetailExaminableEui()
        {
            _window = new DetailExaminableEditWindow();
            _window.OnSave += OnSavePressed;
            _window.OnClose += OnWindowClosed;
        }

        private void OnSavePressed(string description)
        {
            SendMessage(new SaveDetailExaminableMessage
            {
                Text = description,
            });

            if (_window.IsOpen)
                _window.Close();
        }

        private void OnWindowClosed()
        {
            SendMessage(new CloseEuiMessage());
        }

        public override void Opened()
        {
            _window.OpenCentered();
        }

        public override void Closed()
        {
            base.Closed();
            _window.Close();
        }

        public override void HandleState(EuiStateBase state)
        {
            if (state is not DetailExaminableEuiState euiState)
                return;

            if (!string.IsNullOrEmpty(euiState.WindowTitle))
                _window.SetWindowTitle(euiState.WindowTitle);

            if (!string.IsNullOrEmpty(euiState.Placeholder))
                _window.SetPlaceholder(euiState.Placeholder);

            // Empty string hides the preview.
            _window.SetExistingMarkings(euiState.ExistingMarkings);

            if (!euiState.Append)
                _window.SetDescription(euiState.CurrentText);
        }
    }
}
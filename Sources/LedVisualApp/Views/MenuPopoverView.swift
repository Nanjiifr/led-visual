import LedVisualCore
import SwiftUI

struct MenuPopoverView: View {
    @Bindable var coordinator: AppCoordinator
    let onOpenSettings: () -> Void
    let onQuit: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            // Header
            HStack {
                HStack(spacing: 8) {
                    Circle()
                        .fill(statusColor)
                        .frame(width: 9, height: 9)
                    Text("LedVisual")
                        .font(.headline)
                }
                Spacer()
                Text(coordinator.settings.hotkey.displayString)
                    .font(.system(.caption, design: .monospaced).weight(.semibold))
                    .padding(.horizontal, 7)
                    .padding(.vertical, 3)
                    .background(Color.secondary.opacity(0.15), in: RoundedRectangle(cornerRadius: 5))
            }

            Text(coordinator.status.label)
                .font(.subheadline)
                .foregroundStyle(.secondary)

            // Setup warnings (only shown when something needs attention)
            if needsSetup {
                VStack(alignment: .leading, spacing: 6) {
                    if !coordinator.hasAPIKey {
                        setupRow(
                            title: "Google AI Studio API key missing",
                            buttonTitle: "Configure",
                            action: onOpenSettings
                        )
                    }
                    if !coordinator.hasAccessibilityPermission {
                        setupRow(
                            title: "Accessibility needed for global hotkey",
                            buttonTitle: "Grant",
                            action: { coordinator.requestAccessibilityPermission() }
                        )
                    }
                    if !coordinator.hasScreenRecordingPermission {
                        setupRow(
                            title: "Screen Recording needed for capture",
                            buttonTitle: "Grant",
                            action: { coordinator.requestScreenRecordingPermission() }
                        )
                    }
                }
                .padding(8)
                .background(Color.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 8))
            }

            Divider()

            // Last capture summary
            VStack(alignment: .leading, spacing: 4) {
                Text("Last Activity")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(coordinator.lastSummary)
                    .font(.caption)
                    .lineLimit(2)
                if let error = coordinator.lastError {
                    Text(error)
                        .font(.caption2)
                        .foregroundStyle(.red)
                        .lineLimit(3)
                }
            }

            Divider()

            // Quick Actions
            VStack(spacing: 6) {
                Button {
                    coordinator.triggerManualScreenshot()
                } label: {
                    Label("Capture Screen Now", systemImage: "camera")
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .buttonStyle(.bordered)
                .disabled(!coordinator.hasAPIKey || !coordinator.hasScreenRecordingPermission)

                HStack(spacing: 6) {
                    Button("Test 3 Blinks") {
                        coordinator.testBlink(answers: [3])
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.small)

                    Button("Test 1, 3") {
                        coordinator.testBlink(answers: [1, 3])
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.small)

                    Button("Test Fail") {
                        coordinator.testErrorBlink()
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.small)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            Divider()

            HStack {
                Button {
                    onOpenSettings()
                } label: {
                    Label("Settings…", systemImage: "gearshape")
                }
                .buttonStyle(.plain)

                Spacer()

                Button("Quit", role: .destructive) {
                    onQuit()
                }
                .buttonStyle(.plain)
                .foregroundStyle(.secondary)
            }
            .font(.subheadline)
        }
        .padding(14)
        .frame(width: 295)
        .onAppear {
            coordinator.refreshStatusFlags()
        }
    }

    private var needsSetup: Bool {
        !coordinator.hasAPIKey
            || !coordinator.hasAccessibilityPermission
            || !coordinator.hasScreenRecordingPermission
    }

    private var statusColor: Color {
        switch coordinator.status {
        case .idle:
            return needsSetup ? .orange : .green
        case .recording:
            return .red
        case .thinking:
            return .blue
        case .blinking:
            return .purple
        }
    }

    @ViewBuilder
    private func setupRow(title: String, buttonTitle: String, action: @escaping () -> Void) -> some View {
        HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(.orange)
                .font(.caption)
            Text(title)
                .font(.caption)
                .fixedSize(horizontal: false, vertical: true)
            Spacer()
            Button(buttonTitle, action: action)
                .controlSize(.mini)
                .buttonStyle(.bordered)
        }
    }
}

import AppKit
import CoreGraphics
import Foundation

final class EventTapManager {
    var behavior: MouseScrollBehavior = .normal

    private var activeTap: CFMachPort?
    private var activeSource: CFRunLoopSource?
    private var passiveTap: CFMachPort?
    private var passiveSource: CFRunLoopSource?
    private var classifier = ScrollSourceClassifier()

    func start() -> Bool {
        if let activeTap, CFMachPortIsValid(activeTap) {
            enableTaps()
            if CGEvent.tapIsEnabled(tap: activeTap) { return true }
        }
        if activeTap != nil || passiveTap != nil {
            stop()
        }

        let context = UnsafeMutableRawPointer(Unmanaged.passUnretained(self).toOpaque())
        let gestureMask = NSEvent.EventTypeMask.gesture.rawValue
        let scrollMask = CGEventMask(1) << CGEventType.scrollWheel.rawValue

        activeTap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .tailAppendEventTap,
            options: .defaultTap,
            eventsOfInterest: scrollMask,
            callback: eventTapCallback,
            userInfo: context
        )

        guard let activeTap else {
            stop()
            return false
        }

        guard let newActiveSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, activeTap, 0) else {
            stop()
            return false
        }
        activeSource = newActiveSource
        CFRunLoopAddSource(CFRunLoopGetMain(), newActiveSource, .commonModes)

        // Gesture observation can require a separate privacy grant on some macOS
        // versions. Attempt it silently, but never let it prevent the primary wheel
        // tap from running with Accessibility permission.
        passiveTap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .tailAppendEventTap,
            options: .listenOnly,
            eventsOfInterest: gestureMask,
            callback: eventTapCallback,
            userInfo: context
        )
        if
            let passiveTap,
            let newPassiveSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, passiveTap, 0)
        {
            passiveSource = newPassiveSource
            CFRunLoopAddSource(CFRunLoopGetMain(), newPassiveSource, .commonModes)
        } else {
            passiveTap = nil
        }

        enableTaps()
        guard CGEvent.tapIsEnabled(tap: activeTap) else {
            stop()
            return false
        }
        return true
    }

    func stop() {
        if let activeSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), activeSource, .commonModes)
        }
        if let passiveSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), passiveSource, .commonModes)
        }
        if let activeTap {
            CFMachPortInvalidate(activeTap)
        }
        if let passiveTap {
            CFMachPortInvalidate(passiveTap)
        }

        activeSource = nil
        passiveSource = nil
        activeTap = nil
        passiveTap = nil
    }

    fileprivate func handle(type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            enableTaps()
            return Unmanaged.passUnretained(event)
        }

        let timestamp = DispatchTime.now().uptimeNanoseconds
        if type.rawValue == UInt32(NSEvent.EventType.gesture.rawValue) {
            if let nsEvent = NSEvent(cgEvent: event) {
                let count = nsEvent.touches(matching: .touching, in: nil).count
                classifier.recordGesture(touchCount: count, timestamp: timestamp)
            }
            return Unmanaged.passUnretained(event)
        }

        guard type == .scrollWheel else {
            return Unmanaged.passUnretained(event)
        }

        let isContinuous = event.getIntegerValueField(.scrollWheelEventIsContinuous) != 0
        let momentumPhase = NSEvent(cgEvent: event)?.momentumPhase ?? []
        let source: ScrollInputSource
        if isContinuous, passiveTap == nil {
            // Without gesture observation, favor trackpad safety. Discrete wheel
            // mice still classify correctly; a Magic Mouse remains system-default
            // until Input Monitoring is granted.
            source = .trackpad
        } else {
            source = classifier.classify(
                isContinuous: isContinuous,
                hasMomentumPhase: !momentumPhase.isEmpty,
                timestamp: timestamp
            )
        }

        guard source == .mouse, shouldInvert(event: event) else {
            return Unmanaged.passUnretained(event)
        }

        invertScrollDeltas(in: event)
        return Unmanaged.passUnretained(event)
    }

    private func shouldInvert(event: CGEvent) -> Bool {
        switch behavior {
        case .systemDefault:
            return false
        case .normal:
            return NSEvent(cgEvent: event)?.isDirectionInvertedFromDevice == true
        case .reversed:
            return NSEvent(cgEvent: event)?.isDirectionInvertedFromDevice == false
        }
    }

    private func invertScrollDeltas(in event: CGEvent) {
        let integerFields: [CGEventField] = [
            .scrollWheelEventDeltaAxis1,
            .scrollWheelEventDeltaAxis2,
            .scrollWheelEventPointDeltaAxis1,
            .scrollWheelEventPointDeltaAxis2
        ]
        let doubleFields: [CGEventField] = [
            .scrollWheelEventFixedPtDeltaAxis1,
            .scrollWheelEventFixedPtDeltaAxis2
        ]

        let integerValues = integerFields.map(event.getIntegerValueField)
        let doubleValues = doubleFields.map(event.getDoubleValueField)

        // Delta fields must be written before point/fixed-point fields; CoreGraphics
        // otherwise derives and overwrites the smooth-scrolling values.
        for (field, value) in zip(integerFields.prefix(2), integerValues.prefix(2)) {
            event.setIntegerValueField(field, value: -value)
        }
        for (field, value) in zip(doubleFields, doubleValues) {
            event.setDoubleValueField(field, value: -value)
        }
        for (field, value) in zip(integerFields.suffix(2), integerValues.suffix(2)) {
            event.setIntegerValueField(field, value: -value)
        }
    }

    private func enableTaps() {
        if let activeTap, !CGEvent.tapIsEnabled(tap: activeTap) {
            CGEvent.tapEnable(tap: activeTap, enable: true)
        }
        if let passiveTap, !CGEvent.tapIsEnabled(tap: passiveTap) {
            CGEvent.tapEnable(tap: passiveTap, enable: true)
        }
    }
}

private let eventTapCallback: CGEventTapCallBack = { _, type, event, userInfo in
    guard let userInfo else {
        return Unmanaged.passUnretained(event)
    }
    let manager = Unmanaged<EventTapManager>.fromOpaque(userInfo).takeUnretainedValue()
    return manager.handle(type: type, event: event)
}

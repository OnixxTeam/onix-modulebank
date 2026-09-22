from perimeter.runloop import ShutdownFlag


def test_starts_unset():
    flag = ShutdownFlag()

    assert flag.requested is False


def test_a_delivered_signal_sets_the_flag():
    # Calling the handler directly instead of raising a real OS signal --
    # installing signal.signal(...) inside a test would fight pytest's own
    # signal handling and only exercises the standard library, not this class.
    flag = ShutdownFlag()

    flag._handle(15, None)

    assert flag.requested is True

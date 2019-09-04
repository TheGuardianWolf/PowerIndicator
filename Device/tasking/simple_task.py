class SimpleTask(object):
    def __init__(self, name, callback, period_ms=1000, oneshot=False):
        self.name = name
        self.callback = callback
        self.oneshot = oneshot
        self.period_ms = period_ms
        self.last_run_ms = 0

# Read-only verifier for the KRTrainer Yearly Action patch.
# Waits until the pending Yearly Action hand is answered, then logs the rare-hand state until the
# Yearly Action backlog is drained (or a timeout), and prints a PASS/FAIL summary.
import sys, time, struct
import live_cardsys as L

WAIT_FOR_ANSWER_S = int(sys.argv[1]) if len(sys.argv) > 1 else 1800
DRAIN_TIMEOUT_S = 240

def snap(p, base):
    for attempt in range(5):  # the game mutates these vectors while we read; retry torn reads
        try:
            return _snap(p, base)
        except L.ReadError:
            if attempt == 4: raise
            time.sleep(0.1)

def _snap(p, base):
    S = L.snapshot(p, base)
    cs = S["cs"]
    bought_first, n = p.vector(cs + 0x110, 0x40)
    bought = {}
    for i in range(n):
        e, = struct.unpack("<H", p.read(bought_first + i * 0x40, 2))
        bought[L.CARD.get(e, hex(e))] = p.i32(bought_first + i * 0x40 + 8)
    q = S["queued"]
    yearly = sum(1 for e in q if e[0] in (35, 36))
    return S, bought, yearly

def line(S, bought, yearly):
    hand = [L.CARD.get(c, hex(c)) for c in S["cardsRareHand"]]
    return ("hand=%s enum=%s queued=%d yearly=%s sticky=%s overlay=%s justRerolled=%s bought=%s" % (
        hand, L.RAREHAND.get(S["rareHandEnum"], S["rareHandEnum"]), len(S["queued"]), yearly, S["stickyUIQueue"], S["overlayVisibility"],
        S["justRerolledRareHand"], bought))

def main():
    pid = L.find_pid()[0]
    p = L.Proc(pid); base, _ = p.module()
    S, bought, yearly = snap(p, base)
    print(time.strftime("%H:%M:%S"), "start", line(S, bought, yearly), flush=True)
    start_hand = list(S["cardsRareHand"])
    t0 = time.time()
    while list(S["cardsRareHand"]) == start_hand and start_hand:
        if time.time() - t0 > WAIT_FOR_ANSWER_S:
            print("TIMEOUT waiting for the current hand to be answered"); return 1
        time.sleep(0.5)
        S, bought, yearly = snap(p, base)
    print(time.strftime("%H:%M:%S"), "answered", line(S, bought, yearly), flush=True)
    t1 = time.time(); last = ""; saw_actions_hand = False
    while time.time() - t1 < DRAIN_TIMEOUT_S:
        S, bought, yearly = snap(p, base)
        cur = line(S, bought, yearly)
        if cur != last: print(time.strftime("%H:%M:%S"), cur, flush=True); last = cur
        if S["cardsRareHand"] and S["rareHandEnum"] in (35, 36): saw_actions_hand = True
        if yearly == 0 and not S["cardsRareHand"]: break
        time.sleep(0.25)
    S, bought, yearly = snap(p, base)
    ok = (not saw_actions_hand) and yearly is not None and yearly <= 2 and S["overlayVisibility"] == 1  # ESlateVisibility::Collapsed
    print(time.strftime("%H:%M:%S"), "final", line(S, bought, yearly))
    print("RESULT:", "PASS" if ok else "FAIL",
          "| non-empty Yearly Action hand seen after answer:", saw_actions_hand,
          "| yearly backlog now:", yearly, "| overlay:", S["overlayVisibility"])
    return 0 if ok else 1

if __name__ == "__main__":
    sys.exit(main())

# Builds placeOrder messages with ib_insync's own code, without connecting anywhere, so ScalpFeed's
# IbkrTrader.PlaceOrderFields can be compared with them field for field. Prints one line per order,
# fields joined with |.
import sys
from ib_insync.client import Client
from ib_insync.contract import Stock
from ib_insync.order import Order


class Capture(Client):
    def __init__(self):
        super().__init__(wrapper=None)
        self.captured = []

    def serverVersion(self):
        return 176

    def isConnected(self):
        return True

    def sendMsg(self, msg):
        self.captured.append(msg)


def build(order_id, action, qty, order_type, lmt=None, aux=None, tif="DAY", outside=False):
    c = Capture()
    o = Order(action=action, totalQuantity=qty, orderType=order_type, tif=tif, account="DU000000", outsideRth=outside)
    if lmt is not None:
        o.lmtPrice = lmt
    if aux is not None:
        o.auxPrice = aux
    c.placeOrder(order_id, Stock("NXXT", "SMART", "USD"), o)
    # Every field ends with a zero byte, so drop only the one after the last field.
    return c.captured[0][:-1].split("\0")


for fields in (
    build(101, "BUY", 100, "LMT", lmt=3.45, outside=True),
    build(102, "SELL", 250, "MKT"),
    build(103, "SELL", 100, "STP", aux=3.1),
    build(104, "BUY", 300, "STP LMT", lmt=0.1234, aux=0.12, tif="GTC", outside=True),
):
    print("|".join(fields))

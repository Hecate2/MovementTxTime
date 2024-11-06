import os
import time
import openpyxl
from openpyxl import Workbook
from openpyxl.worksheet.worksheet import Worksheet


def handle_dir(dir: str):
    xlsx_files = [f for f in os.listdir(dir) if os.path.isfile(f) and f.lower().endswith('.xlsx')]
    for f in xlsx_files:
        wb = openpyxl.load_workbook(f, read_only=True, data_only=True)
        for sheet in wb:
            handle_sheet(sheet)

def handle_sheet(sheet: Worksheet):
    txn_tag = sheet["C1"]
    print(txn_tag)

handle_dir('.')
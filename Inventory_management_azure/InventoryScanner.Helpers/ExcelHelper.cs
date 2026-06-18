using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;

namespace InventoryScanner.Helpers;

public static class ExcelHelper
{
	public static MemoryStream ToExcelStream(List<Dictionary<string, object?>> data, string sheetName = "Sheet1")
	{
		using XLWorkbook xLWorkbook = new XLWorkbook();
		IXLWorksheet iXLWorksheet = xLWorkbook.Worksheets.Add(sheetName);
		if (data.Count == 0)
		{
			MemoryStream memoryStream = new MemoryStream();
			xLWorkbook.SaveAs(memoryStream);
			memoryStream.Position = 0L;
			return memoryStream;
		}
		List<string> list = data[0].Keys.ToList();
		for (int i = 0; i < list.Count; i++)
		{
			iXLWorksheet.Cell(1, i + 1).Value = list[i];
		}
		for (int j = 0; j < data.Count; j++)
		{
			for (int k = 0; k < list.Count; k++)
			{
				object valueOrDefault = data[j].GetValueOrDefault(list[k]);
				iXLWorksheet.Cell(j + 2, k + 1).Value = ConvertToXLValue(valueOrDefault);
			}
		}
		iXLWorksheet.Columns().AdjustToContents();
		MemoryStream memoryStream2 = new MemoryStream();
		xLWorkbook.SaveAs(memoryStream2);
		memoryStream2.Position = 0L;
		return memoryStream2;
	}

	private static XLCellValue ConvertToXLValue(object? value)
	{
		if (value != null)
		{
			if (!(value is string text))
			{
				if (!(value is int num))
				{
					if (!(value is long num2))
					{
						if (!(value is double num3))
						{
							if (!(value is float num4))
							{
								if (!(value is decimal num5))
								{
									if (!(value is bool flag))
									{
										if (!(value is DateTime dateTime))
										{
											if (value is DateTimeOffset dateTimeOffset)
											{
												return dateTimeOffset.DateTime;
											}
											return value.ToString() ?? string.Empty;
										}
										return dateTime;
									}
									return flag;
								}
								return (double)num5;
							}
							return num4;
						}
						return num3;
					}
					return num2;
				}
				return num;
			}
			return text;
		}
		return Blank.Value;
	}
}

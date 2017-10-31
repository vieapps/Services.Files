#region Related component
using System;
using System.IO;
using System.Web;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Collections.Generic;

using net.vieapps.Components.Utility;
using net.vieapps.Components.Security;
#endregion

namespace net.vieapps.Services.Files
{
	public class CaptchaHandler : AbstractHttpHandler
	{
		public override async Task ProcessRequestAsync(HttpContext context, CancellationToken cancellationToken = default(CancellationToken))
		{
			// check
			if (!context.Request.HttpMethod.IsEquals("GET"))
				throw new InvalidRequestException();

			// prepare
			var requestUrl = context.Request.RawUrl.Substring(context.Request.ApplicationPath.Length);
			while (requestUrl.StartsWith("/"))
				requestUrl = requestUrl.Right(requestUrl.Length - 1);
			if (requestUrl.IndexOf("?") > 0)
				requestUrl = requestUrl.Left(requestUrl.IndexOf("?"));

			var requestInfo = requestUrl.ToArray('/', true);
			var useSmallImage = true;
			if (requestInfo.Length > 2)
				try
				{
					useSmallImage = !requestInfo[2].Url64Decode().IsEquals("big");
				}
				catch { }

			// generate captcha image
			var image = CaptchaHandler.GenerateImage(requestInfo[1].Url64Decode(), useSmallImage);

			// write to output stream
			context.Response.Cache.SetNoStore();
			context.Response.ContentType = "image/jpeg";
			await context.Response.OutputStream.WriteAsync(image, 0, image.Length);
		}

		#region Generate captcha image
		/// <summary>
		/// Generates captcha images
		/// </summary>
		/// <param name="code">The string that presents encrypted code for generating</param>
		/// <param name="useSmallImage">true to use small image</param>
		/// <param name="noises">The collection of noise texts</param>
		/// <returns>The stream that contains captcha image in JPEG format</returns>
		public static byte[] GenerateImage(string code, bool useSmallImage = true, List<string> noises = null)
		{
			// check code
			if (!code.Equals(""))
			{
				try
				{
					code = code.Decrypt(CryptoService.DefaultEncryptionKey, true).ToArray('-').Last();
					var tempCode = "";
					var space = " ";
					var spaceP = "";
					if (code.Length <= 5 && !useSmallImage)
						spaceP = "  ";
					else if (useSmallImage)
						space = "";
					for (int index = 0; index < code.Length; index++)
						tempCode += spaceP + code[index].ToString() + space;
					code = tempCode;
				}
				catch
				{
					code = "I-n-valid";
				}
			}
			else
				code = "Invalid";

			// prepare sizae of security image
			var width = 220;
			var height = 48;
			if (useSmallImage)
			{
				width = 110;
				height = 24;
			}

			// create new graphic from the bitmap with random background color
			var backgroundColors = useSmallImage
				? new Color[] { Color.Orange, Color.Thistle, Color.LightSeaGreen, Color.Yellow, Color.YellowGreen, Color.NavajoWhite, Color.White }
				: new Color[] { Color.Orange, Color.Thistle, Color.LightSeaGreen, Color.Violet, Color.Yellow, Color.YellowGreen, Color.NavajoWhite, Color.LightGray, Color.Tomato, Color.LightGreen, Color.White };

			var securityBitmap = CaptchaHandler.CreateBackroundImage(width, height, new Color[] { backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)], backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)], backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)], backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)] });
			var securityGraph = Graphics.FromImage(securityBitmap);
			securityGraph.SmoothingMode = SmoothingMode.AntiAlias;

			// add noise texts (for big image)
			if (!useSmallImage)
			{
				// comuting noise texts for the image
				var texts = noises != null && noises.Count > 0
					? noises
					: new List<string>() { "VIEApps", "vieapps.net", "VIEApps API Gateway", "VIEApps REST API" };

				var noiseTexts = new List<string>() { "Winners never quit", "Quitters never win", "Vietnam - The hidden charm", "Don't be evil", "Keep moving", "Connecting People", "Information at your fingertips", "No sacrifice no victory", "No paint no gain", "Enterprise Web Services", "On-Demand Services for Enterprise", "Cloud Computing Enterprise Services", "Where do you want to go today?", "Make business easier", "Simplify business process", "VIEApps", "vieapps.net" };
				noiseTexts.Append(texts);

				var noiseText = noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)];
				noiseText += " " + noiseText + " " + noiseText + " " + noiseText;

				// write noise texts
				securityGraph.DrawString(noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)] + " - " + noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)], new Font("Verdana", 10, FontStyle.Underline), new SolidBrush(Color.White), new PointF(0, 3));
				securityGraph.DrawString(noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)] + " - " + noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)], new Font("Verdana", 12, FontStyle.Bold), new SolidBrush(Color.White), new PointF(5, 7));
				securityGraph.DrawString(noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)] + " - " + noiseTexts[UtilityService.GetRandomNumber(0, noiseTexts.Count)], new Font("Arial", 11, FontStyle.Italic), new SolidBrush(Color.White), new PointF(-5, 20));
				securityGraph.DrawString(noiseText, new Font("Arial", 12, FontStyle.Bold), new SolidBrush(Color.White), new PointF(20, 28));
			}

			// add noise lines (for small image)
			else
			{
				// randrom index to make noise lines
				var randomIndex = UtilityService.GetRandomNumber(0, backgroundColors.Length);

				// first two lines
				var noisePen = new Pen(new SolidBrush(Color.Gray), 2);
				securityGraph.DrawLine(noisePen, new Point(width, randomIndex), new Point(randomIndex, height / 2 - randomIndex));
				securityGraph.DrawLine(noisePen, new Point(width / 3 - randomIndex, randomIndex), new Point(width / 2 + randomIndex, height - randomIndex));

				// second two lines
				noisePen = new Pen(new SolidBrush(Color.Yellow), 1);
				securityGraph.DrawLine(noisePen, new Point(((width / 4) * 3) - randomIndex, randomIndex), new Point(width / 3 + randomIndex, height - randomIndex));
				if (randomIndex % 2 == 1)
					securityGraph.DrawLine(noisePen, new Point(width - randomIndex * 2, randomIndex), new Point(randomIndex, height - randomIndex));
				else
					securityGraph.DrawLine(noisePen, new Point(randomIndex, randomIndex), new Point(width - randomIndex * 2, height - randomIndex));

				// third two lines
				randomIndex = UtilityService.GetRandomNumber(0, backgroundColors.Length);
				noisePen = new Pen(new SolidBrush(Color.Magenta), 1);
				securityGraph.DrawLine(noisePen, new Point(((width / 6) * 3) - randomIndex, randomIndex), new Point(width / 5 + randomIndex, height - randomIndex + 3));
				if (randomIndex % 2 == 1)
					securityGraph.DrawLine(noisePen, new Point(width - randomIndex * 2, randomIndex - 1), new Point(randomIndex, height - randomIndex - 3));
				else
					securityGraph.DrawLine(noisePen, new Point(randomIndex, randomIndex + 1), new Point(width - randomIndex * 2, height - randomIndex + 4));

				// fourth two lines
				randomIndex = UtilityService.GetRandomNumber(0, backgroundColors.Length);
				noisePen = new Pen(new SolidBrush(backgroundColors[UtilityService.GetRandomNumber(0, backgroundColors.Length)]), 1);
				securityGraph.DrawLine(noisePen, new Point(((width / 10) * 3) - randomIndex, randomIndex), new Point(width / 6 + randomIndex, height - randomIndex + 3));
				if (randomIndex % 2 == 1)
					securityGraph.DrawLine(noisePen, new Point(width - randomIndex * 3, randomIndex - 2), new Point(randomIndex, height - randomIndex - 2));
				else
					securityGraph.DrawLine(noisePen, new Point(randomIndex, randomIndex + 2), new Point(width - randomIndex * 3, height - randomIndex + 2));
			}

			// put the security code into the image with random font and brush
			var fonts = new string[] { "Verdana", "Arial", "Times New Roman", "Courier", "Courier New", "Comic Sans MS" };

			var brushs = new Brush[] {
				new SolidBrush(Color.Black), new SolidBrush(Color.Blue), new SolidBrush(Color.DarkBlue), new SolidBrush(Color.DarkGreen),
				new SolidBrush(Color.Magenta), new SolidBrush(Color.Red), new SolidBrush(Color.DarkRed), new SolidBrush(Color.Black),
				new SolidBrush(Color.Firebrick), new SolidBrush(Color.DarkGreen), new SolidBrush(Color.Green), new SolidBrush(Color.DarkViolet)
			};

			if (useSmallImage)
			{
				var step = 0;
				for (var index = 0; index < code.Length; index++)
				{
					float x = (index * 7) + step + UtilityService.GetRandomNumber(-1, 9);
					float y = UtilityService.GetRandomNumber(-2, 0);

					var writtenCode = code.Substring(index, 1);
					if (writtenCode.Equals("I") || (UtilityService.GetRandomNumber() % 2 == 1 && !writtenCode.Equals("L")))
						writtenCode = writtenCode.ToLower();

					var addedX = UtilityService.GetRandomNumber(-3, 5);
					securityGraph.DrawString(writtenCode, new Font(fonts[UtilityService.GetRandomNumber(0, fonts.Length)], UtilityService.GetRandomNumber(13, 19), FontStyle.Bold), brushs[UtilityService.GetRandomNumber(0, brushs.Length)], new PointF(x + addedX, y));
					step += UtilityService.GetRandomNumber(13, 23);
				}
			}
			else
			{
				// write code
				var step = 0;
				for (int index = 0; index < code.Length; index++)
				{
					var font = fonts[UtilityService.GetRandomNumber(0, fonts.Length)];
					float x = 2 + step, y = 10;
					step += 9;
					float fontSize = 15;
					if (index > 1 && index < 4)
					{
						fontSize = 25;
						x -= 10;
						y -= 5;
					}
					else if (index > 3 && index < 6)
					{
						y -= UtilityService.GetRandomNumber(3, 5);
						fontSize += index;
						step += index / 5;
						if (index == 4)
						{
							if (UtilityService.GetRandomNumber() % 2 == 1)
								y += UtilityService.GetRandomNumber(8, 12);
							else if (UtilityService.GetRandomNumber() % 2 == 2)
							{
								y -= UtilityService.GetRandomNumber(2, 6);
								fontSize += UtilityService.GetRandomNumber(1, 4);
							}
						}
					}
					else if (index > 5)
					{
						x += UtilityService.GetRandomNumber(0, 4);
						y -= UtilityService.GetRandomNumber(0, 4);
						fontSize += index - 7;
						step += index / 3 + 1;
						if (index == 10)
						{
							if (UtilityService.GetRandomNumber() % 2 == 1)
								y += UtilityService.GetRandomNumber(7, 14);
							else if (UtilityService.GetRandomNumber() % 2 == 2)
							{
								y -= UtilityService.GetRandomNumber(1, 3);
								fontSize += UtilityService.GetRandomNumber(2, 5);
							}
						}
					}
					var writtenCode = code.Substring(index, 1);
					if (writtenCode.Equals("I") || (UtilityService.GetRandomNumber() % 2 == 1 && !writtenCode.Equals("L")))
						writtenCode = writtenCode.ToLower();
					securityGraph.DrawString(writtenCode, new Font(font, fontSize, FontStyle.Bold), brushs[UtilityService.GetRandomNumber(0, brushs.Length)], new PointF(x + 2, y + 2));
					securityGraph.DrawString(writtenCode, new Font(font, fontSize, FontStyle.Bold), brushs[UtilityService.GetRandomNumber(0, brushs.Length)], new PointF(x, y));
				}

				// fill it randomly with pixels
				int maxX = width, maxY = height, startX = 0, startY = 0;
				int random = UtilityService.GetRandomNumber(1, 100);
				if (random > 80)
				{
					maxX -= maxX / 3;
					maxY = maxY / 2;
				}
				else if (random > 60)
				{
					startX = maxX / 3;
					startY = maxY / 2;
				}
				else if (random > 30)
				{
					startX = maxX / 7;
					startY = maxY / 4;
					maxX -= maxX / 5;
					maxY -= maxY / 8;
				}

				for (int iX = startX; iX < maxX; iX++)
					for (int iY = startY; iY < maxY; iY++)
						if ((iX % 3 == 1) && (iY % 4 == 1))
							securityBitmap.SetPixel(iX, iY, Color.DarkGray);
			}

			// add random noise into image (use SIN)
			double divideTo = 64.0d + UtilityService.GetRandomNumber(1, 10);
			int distortion = UtilityService.GetRandomNumber(5, 11);
			if (useSmallImage)
				distortion = UtilityService.GetRandomNumber(1, 5);

			var noisedBitmap = new Bitmap(width, height, PixelFormat.Format16bppRgb555);
			for (int y = 0; y < height; y++)
				for (int x = 0; x < width; x++)
				{
					int newX = (int)(x + (distortion * Math.Sin(Math.PI * y / divideTo)));
					if (newX < 0 || newX >= width)
						newX = 0;

					int newY = (int)(y + (distortion * Math.Cos(Math.PI * x / divideTo)));
					if (newY < 0 || newY >= height)
						newY = 0;

					noisedBitmap.SetPixel(x, y, securityBitmap.GetPixel(newX, newY));
				}

			// export as JPEG image
			using (var stream = new MemoryStream())
			{
				noisedBitmap.Save(stream, ImageFormat.Jpeg);
				securityGraph.Dispose();
				securityBitmap.Dispose();
				noisedBitmap.Dispose();
				return stream.ToArray();
			}
		}

		static Bitmap CreateBackroundImage(int width, int height, Color[] backgroundColors)
		{
			// create element bitmaps
			int bmpWidth = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(5, 10), UtilityService.GetRandomNumber(20, width / 2));
			int bmpHeight = UtilityService.GetRandomNumber(height / 4, height / 2);
			if (height > 20)
				bmpHeight = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(1, 10), UtilityService.GetRandomNumber(12, height));
			var bitmap1 = new Bitmap(bmpWidth, bmpHeight, PixelFormat.Format16bppRgb555);
			var graph = Graphics.FromImage(bitmap1);
			graph.SmoothingMode = SmoothingMode.AntiAlias;
			graph.Clear(backgroundColors[0]);

			bmpWidth = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(15, width / 3), UtilityService.GetRandomNumber(width / 3, width / 2));
			bmpHeight = UtilityService.GetRandomNumber(5, height / 3);
			if (height > 20)
				bmpHeight = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(5, height / 4), UtilityService.GetRandomNumber(height / 4, height / 2));
			var bitmap2 = new Bitmap(bmpWidth, bmpHeight, PixelFormat.Format16bppRgb555);
			graph = Graphics.FromImage(bitmap2);
			graph.SmoothingMode = SmoothingMode.AntiAlias;
			graph.Clear(backgroundColors[1]);

			bmpWidth = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(width / 4, width / 2), UtilityService.GetRandomNumber(width / 2, width));
			bmpHeight = UtilityService.GetRandomNumber(height / 2, height);
			if (height > 20)
				bmpHeight = UtilityService.GetRandomNumber(UtilityService.GetRandomNumber(height / 5, height / 2), UtilityService.GetRandomNumber(height / 2, height));
			var bitmap3 = new Bitmap(bmpWidth, bmpHeight, PixelFormat.Format16bppRgb555);
			graph = Graphics.FromImage(bitmap3);
			graph.SmoothingMode = SmoothingMode.AntiAlias;
			graph.Clear(backgroundColors[2]);

			var backroundBitmap = new Bitmap(width, height, PixelFormat.Format16bppRgb555);
			graph = Graphics.FromImage(backroundBitmap);
			graph.SmoothingMode = SmoothingMode.AntiAlias;
			graph.Clear(backgroundColors[3]);
			graph.DrawImage(bitmap1, UtilityService.GetRandomNumber(0, width / 2), UtilityService.GetRandomNumber(0, height / 2));
			graph.DrawImage(bitmap2, UtilityService.GetRandomNumber(width / 5, width / 2), UtilityService.GetRandomNumber(height / 5, height / 2));
			graph.DrawImage(bitmap3, UtilityService.GetRandomNumber(width / 4, width / 3), UtilityService.GetRandomNumber(0, height / 3));

			return backroundBitmap;
		}
		#endregion

	}
}
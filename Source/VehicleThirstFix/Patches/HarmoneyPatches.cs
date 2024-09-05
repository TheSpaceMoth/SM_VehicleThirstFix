using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DubsBadHygiene;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Vehicles;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace SM.VehicleThirstFix
{
	[StaticConstructorOnStartup]
	static public class HarmonyPatches
	{
		public static Harmony harmonyInstance;
		
		static HarmonyPatches()
		{
			harmonyInstance = new Harmony("Spacemoth.VehicleThirstFix");
			harmonyInstance.PatchAll();
		}
	}

	[HarmonyPatch(typeof(Caravan_NeedsTracker), "TrySatisfyPawnsNeeds")]
	internal static class PatchedTrySatisfyPawnNeeds
	{
		[HarmonyPostfix]
		public static void PostFix(Caravan_NeedsTracker __instance)
		{
			List<Pawn> PawnList = new List<Pawn>();
			PawnList.AddRange((IEnumerable<Pawn>)__instance.caravan.PawnsListForReading.ToList<Pawn>());
			//PawnList.Sort((Comparison<Pawn>)((p1, p2) => p1.RaceProps.Humanlike.CompareTo(p2.RaceProps.Humanlike)));

			for (int i = 0; i < PawnList.Count; i++)
				HandleNeeds(PawnList[i]);
		}

		private static void HandleNeeds(Pawn pawn)
		{
			if (pawn.Dead)
				return;

			Caravan pawnsCaravan = pawn.GetCaravan();

			if (pawnsCaravan is VehicleCaravan)
			{
				VehicleCaravan VCaravan = (VehicleCaravan)pawnsCaravan;
				bool FreeRefill = false;

				// The ground does not matter if flying.
				if (VCaravan.AerialVehicle == false)
				{
					if (Find.WorldGrid[VCaravan.Tile].Rivers.NullOrEmpty<Tile.RiverLink>() == false)
					{
						// We have a river
						FreeRefill = true;
					}
					else
					{
						// Check if its raining
						float Rainfall = Find.WorldGrid[VCaravan.Tile].rainfall;

						// If 2000, always raining
						if (Rainfall > 2000f)
							Rainfall = 2000f;

						// If 0 never raining
						if (Rainfall < 0)
							Rainfall = 0;

						// Check the rain
						float ActualRain = Rand.Range(0, 2000f);
						if (ActualRain < Rainfall)
							FreeRefill = true;
					}

					Need_Bladder BNeed = pawn.needs.TryGetNeed<Need_Bladder>();

					if (BNeed != null)
					{
						// If you need to go.. 
						if (BNeed.CurLevel < 0.5f)
						{
							BNeed.CurLevel = 1.0f;
						}
					}
				}
				else
				{
					Need_Bladder BNeed = pawn.needs.TryGetNeed<Need_Bladder>();

					if (BNeed != null)
					{
						// If you need to go but are airborn.. 
						if (BNeed.CurLevel < 0.2f)
						{
							BNeed.CurLevel = 1.0f;
							pawn.inventory.innerContainer.TryAdd(ThingMaker.MakeThing(DubDef.BedPan), 1, true);
						}
					}
				}

					// Tend to thirst first
					Need_Thirst TNeed = pawn.needs.TryGetNeed<Need_Thirst>();

				if (TNeed != null)
				{
					bool ThirstNeeded = (TNeed.CurCategory == ThirstCategory.Thirsty) ? true : false;

					// First check for any clean water
					if (ThirstNeeded == true)
					{
						// Use a water source carried by the caravan
						ThirstNeeded = DrinkMiniWater(pawn, TNeed, VCaravan, ContaminationLevel.Treated);

						// Check for a bottle
						if (ThirstNeeded == true)
						{
							ThirstNeeded = DrinkBottleWater(pawn, VCaravan, ContaminationLevel.Treated);
						}
					}

					// Check for any untreated water sources
					if (ThirstNeeded == true)
					{
						// Check for a free refill.
						if (FreeRefill == true)
						{
							TNeed.CurLevel = 1.0f;
							SanitationUtil.ContaminationCheckWater(pawn, ContaminationLevel.Untreated);
							ThirstNeeded = false;
						}
						else
						{
							// Use a water source carried by the caravan
							ThirstNeeded = DrinkMiniWater(pawn, TNeed, VCaravan, ContaminationLevel.Untreated);

							// Check for a bottle
							if (ThirstNeeded == true)
							{
								ThirstNeeded = DrinkBottleWater(pawn, VCaravan, ContaminationLevel.Untreated);
							}
						}
					}

					if (ThirstNeeded == true)
					{
						// Going to need to be really thirsty to drink tainted water..
						if (TNeed.CurLevel < 0.1)
						{
							// Use a water source carried by the caravan
							ThirstNeeded = DrinkMiniWater(pawn, TNeed, VCaravan, ContaminationLevel.Contaminated);

							// Check for a bottle
							if (ThirstNeeded == true)
							{
								ThirstNeeded = DrinkBottleWater(pawn, VCaravan, ContaminationLevel.Contaminated);
							}
						}
					}
				}

				Need_Hygiene HNeed = pawn.needs.TryGetNeed<Need_Hygiene>();

				if (HNeed != null)
				{
					if (FreeRefill == true)
					{
						// May as well top up while we can
						if (HNeed.CurLevel < 0.7f)
							HNeed.CurLevel = 0.7f;
					}
					else if (HNeed.CurLevel < 0.7f)
					{
						// Use a water source carried by the caravan
						List<Thing> waterSources = CaravanInventoryUtility.AllInventoryItems(VCaravan).FindAll((Predicate<Thing>)(x => x.GetInnerIfMinified().HasComp<CompWaterStorage>()));

						foreach (Thing waterSource in waterSources)
						{
							Thing actualWaterSource = waterSource.GetInnerIfMinified();
							float WaterWithin = actualWaterSource.TryGetComp<CompWaterStorage>().WaterStorage;

							// Found some water to use.
							if (WaterWithin > 0)
							{
								actualWaterSource.TryGetComp<CompWaterStorage>().WaterStorage -= 1.0f;
								HNeed.CurLevel = 0.7f;
								break;
							}
						}
					}
				}
			}
			else
			{
				//Log.Message("No vehicles");
			}

			return;
		}

		static bool DrinkMiniWater(Pawn pawn, Need_Thirst TNeed, VehicleCaravan VCaravan, ContaminationLevel Purity)
		{
			bool StillThirsty = true;

			// Use a water source carried by the caravan
			List<Thing> waterSources = CaravanInventoryUtility.AllInventoryItems(VCaravan).FindAll((Predicate<Thing>)(x => x.GetInnerIfMinified().HasComp<CompWaterStorage>()));

			foreach (Thing waterSource in waterSources)
			{
				Thing actualWaterSource = waterSource.GetInnerIfMinified();

				// Check for good water
				if (actualWaterSource.TryGetComp<CompWaterStorage>().WaterQuality == Purity)
				{
					float WaterWithin = actualWaterSource.TryGetComp<CompWaterStorage>().WaterStorage;

					// Found some water to use.
					if (WaterWithin > 0)
					{
						actualWaterSource.TryGetComp<CompWaterStorage>().WaterStorage -= 0.5f;
						TNeed.CurLevel = 1.0f;
						SanitationUtil.ContaminationCheckWater(pawn, Purity);
						StillThirsty = false;
						break;
					}
				}
			}

			return (StillThirsty);
		}

		static bool DrinkBottleWater(Pawn pawn, VehicleCaravan VCaravan, ContaminationLevel Purity)
		{
			bool StillThirsty = true;

			// Use a water source carried by the caravan
			List<Thing> waterSources = CaravanInventoryUtility.AllInventoryItems(VCaravan).FindAll((Predicate<Thing>)(x => ThirstUsable(x.def)));

			foreach (Thing waterSource in waterSources)
			{
				CompContamination contComp = waterSource.TryGetComp<CompContamination>();
				bool BottleDrinkable = false;

				if (contComp != null)
				{
					// Check contamination
					if (contComp.level == Purity)
					{
						// Drink it!
						BottleDrinkable = true;
					}
				}
				else if(Purity == ContaminationLevel.Untreated)
				{
					// if not set, assume untreated
					BottleDrinkable = true;
				}

				if(BottleDrinkable == true)
				{
					Pawn BottleOwner = CaravanInventoryUtility.GetOwnerOf(VCaravan, waterSource);
					waterSource.Ingested(pawn, pawn.needs.food.NutritionWanted);
					if ((waterSource.Destroyed == true) && (BottleOwner != null))
					{
						BottleOwner.inventory.innerContainer.Remove(waterSource);
						StillThirsty = false;
						SanitationUtil.ContaminationCheckWater(pawn, Purity);
						VCaravan.RecacheImmobilizedNow();
						VCaravan.RecacheDaysWorthOfFood();
					}
				}
			}

			return (StillThirsty);
		}

		static bool ThirstUsable(ThingDef thing)
		{
			WaterExt WaterComp = thing.GetModExtension<WaterExt>();

			if (WaterComp != null)
			{
				if (WaterComp.SeekForThirst == true)
					return (true);
				else
					return (false);
			}
			else
			{
				return (false);
			}
		}
	}


}
